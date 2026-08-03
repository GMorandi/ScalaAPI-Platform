using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace Sub2Api.Host.Services;

public interface IGarnetService
{
    void Set(string key, string value, TimeSpan? ttl = null);
    string? Get(string key);
    void Delete(string key);
    long Increment(string key)
    {
        var current = long.TryParse(Get(key), out var value) ? value : 0;
        var next = current + 1;
        Set(key, next.ToString());
        return next;
    }
}

public class EmbeddedGarnetService : IGarnetService, Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ConcurrentDictionary<string, (string Value, long? ExpiresAt)> _store = new();
    private readonly string _socketPath;
    private readonly ILogger<EmbeddedGarnetService> _logger;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public EmbeddedGarnetService(string socketPath, ILogger<EmbeddedGarnetService> logger)
    {
        _socketPath = socketPath;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(128);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_cts.Token);

        _logger.LogInformation("Embedded Garnet RESP server listening on {Path}", _socketPath);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
        _store.Clear();
        _logger.LogInformation("Embedded Garnet RESP server stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Garnet accept error");
            }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        using var stream = new NetworkStream(client, ownsSocket: true);
        var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var args = await ReadCommandAsync(reader, ct);
                if (args is null || args.Count == 0) break;

                var response = ExecuteCommand(args);
                await writer.WriteAsync(response);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Garnet client disconnected");
        }
    }

    private static async Task<List<string>?> ReadCommandAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line is null) return null;

        if (line.StartsWith('*'))
        {
            var count = int.Parse(line[1..]);
            var args = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                var bulkHeader = await reader.ReadLineAsync(ct);
                if (bulkHeader is null || !bulkHeader.StartsWith('$')) return null;
                var len = int.Parse(bulkHeader[1..]);
                var buf = new char[len];
                var read = 0;
                while (read < len)
                {
                    var n = await reader.ReadAsync(buf.AsMemory(read, len - read), ct);
                    if (n == 0) return null;
                    read += n;
                }
                await reader.ReadLineAsync(ct); // consume trailing \r\n
                args.Add(new string(buf));
            }
            return args;
        }

        // Inline command (e.g., "PING")
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private string ExecuteCommand(List<string> args)
    {
        var cmd = args[0].ToUpperInvariant();

        switch (cmd)
        {
            case "PING":
                return "+PONG\r\n";

            case "GET" when args.Count >= 2:
            {
                var val = Get(args[1]);
                if (val is null) return "$-1\r\n";
                return $"${Encoding.UTF8.GetByteCount(val)}\r\n{val}\r\n";
            }

            case "SET" when args.Count >= 3:
            {
                TimeSpan? ttl = null;
                for (int i = 3; i < args.Count - 1; i++)
                {
                    if (args[i].Equals("EX", StringComparison.OrdinalIgnoreCase))
                        ttl = TimeSpan.FromSeconds(int.Parse(args[i + 1]));
                    else if (args[i].Equals("PX", StringComparison.OrdinalIgnoreCase))
                        ttl = TimeSpan.FromMilliseconds(int.Parse(args[i + 1]));
                }
                Set(args[1], args[2], ttl);
                return "+OK\r\n";
            }

            case "DEL" when args.Count >= 2:
            {
                int deleted = 0;
                for (int i = 1; i < args.Count; i++)
                {
                    if (_store.TryRemove(args[i], out _)) deleted++;
                }
                return $":{deleted}\r\n";
            }

            case "INCR" when args.Count >= 2:
            {
                var value = Increment(args[1]);
                return $":{value}\r\n";
            }

            case "MGET" when args.Count >= 2:
            {
                var sb = new StringBuilder();
                sb.Append($"*{args.Count - 1}\r\n");
                for (int i = 1; i < args.Count; i++)
                {
                    var val = Get(args[i]);
                    if (val is null) sb.Append("$-1\r\n");
                    else sb.Append($"${Encoding.UTF8.GetByteCount(val)}\r\n{val}\r\n");
                }
                return sb.ToString();
            }

            default:
                return "-ERR unknown command\r\n";
        }
    }

    public void Set(string key, string value, TimeSpan? ttl = null)
    {
        long? expiresAt = ttl.HasValue
            ? DateTimeOffset.UtcNow.Add(ttl.Value).ToUnixTimeMilliseconds()
            : null;
        _store[key] = (value, expiresAt);
    }

    public string? Get(string key)
    {
        if (!_store.TryGetValue(key, out var entry))
            return null;

        if (entry.ExpiresAt.HasValue &&
            entry.ExpiresAt.Value < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            _store.TryRemove(key, out _);
            return null;
        }

        return entry.Value;
    }

    public void Delete(string key)
    {
        _store.TryRemove(key, out _);
    }

    public long Increment(string key)
    {
        while (true)
        {
            var currentText = Get(key) ?? "0";
            var current = long.TryParse(currentText, out var parsed) ? parsed : 0;
            var next = current + 1;
            if (_store.TryUpdate(key, (next.ToString(), null), (currentText, null)) ||
                _store.TryAdd(key, (next.ToString(), null)))
                return next;
        }
    }
}
