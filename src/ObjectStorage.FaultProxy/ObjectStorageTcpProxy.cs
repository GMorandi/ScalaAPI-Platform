using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ScalaAPI.ObjectStorage.FaultProxy;

public sealed class ObjectStorageTcpProxy(
    IConfiguration configuration,
    FaultProxyState state,
    ILogger<ObjectStorageTcpProxy> logger) : BackgroundService
{
    private readonly string _upstreamHost =
        configuration["FaultProxy:UpstreamHost"]?.Trim() is { Length: > 0 } host
            ? host : "object-storage";
    private readonly int _upstreamPort = Math.Clamp(
        configuration.GetValue("FaultProxy:UpstreamPort", 9000), 1, 65535);
    private readonly int _listenPort = Math.Clamp(
        configuration.GetValue("FaultProxy:ListenPort", 9000), 1, 65535);
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private TcpListener? _listener;
    private long _connectionId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Any, _listenPort);
        _listener.Start(256);
        logger.LogInformation(
            "Object-storage fault proxy listening on {ListenPort} for {Host}:{Port}",
            _listenPort, _upstreamHost, _upstreamPort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                var id = Interlocked.Increment(ref _connectionId);
                var task = HandleSafeAsync(client, stoppingToken);
                _connections[id] = task;
                _ = task.ContinueWith(completed =>
                        _connections.TryRemove(id, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _listener?.Stop();
        var active = _connections.Values.ToArray();
        if (active.Length > 0)
            await Task.WhenAny(Task.WhenAll(active), Task.Delay(TimeSpan.FromSeconds(5),
                cancellationToken));
    }

    private async Task HandleSafeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await HandleAsync(client, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Object-storage proxy connection failed");
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        await using var clientStream = client.GetStream();
        var requestBytes = await HttpWireProtocol.ReadHeadAsync(clientStream, ct);
        if (requestBytes is null) return;
        var request = HttpWireProtocol.ParseRequest(requestBytes);
        var fault = state.TryConsume(request.Method, request.Path);

        using var upstream = new TcpClient { NoDelay = true };
        await upstream.ConnectAsync(_upstreamHost, _upstreamPort, ct);
        await using var upstreamStream = upstream.GetStream();
        await upstreamStream.WriteAsync(
            HttpWireProtocol.WithConnectionClose(request.Bytes), ct);

        if (fault?.Mode == ObjectStorageFaultModes.TruncateRequest)
        {
            var cutoff = request.ContentLength > 0
                ? Math.Min(fault.RequestBodyBytes, request.ContentLength - 1)
                : 0;
            var forwarded = await CopyExactlyAsync(clientStream, upstreamStream,
                cutoff, ct);
            await upstreamStream.FlushAsync(ct);
            state.Record(request.Method, request.Path, fault.Mode, forwarded);
            Abort(upstream);
            Abort(client);
            return;
        }

        await CopyExactlyAsync(clientStream, upstreamStream, request.ContentLength, ct);
        await upstreamStream.FlushAsync(ct);
        var responseBytes = await HttpWireProtocol.ReadHeadAsync(upstreamStream, ct)
            ?? throw new EndOfStreamException("upstream closed without an HTTP response");
        var status = HttpWireProtocol.ParseResponseStatus(responseBytes);

        if (fault?.Mode == ObjectStorageFaultModes.DropResponse)
        {
            state.Record(request.Method, request.Path, fault.Mode,
                request.ContentLength, status);
            Abort(client);
            return;
        }

        await clientStream.WriteAsync(responseBytes, ct);
        await upstreamStream.CopyToAsync(clientStream, ct);
        await clientStream.FlushAsync(ct);
        state.Record(request.Method, request.Path, "pass",
            request.ContentLength, status);
    }

    private static async Task<long> CopyExactlyAsync(Stream input, Stream output,
        long length, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var copied = 0L;
        while (copied < length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, length - copied)), ct);
            if (read == 0) throw new EndOfStreamException(
                $"request body ended after {copied} of {length} bytes");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
        }
        return copied;
    }

    private static void Abort(TcpClient client)
    {
        try
        {
            client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Close();
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
