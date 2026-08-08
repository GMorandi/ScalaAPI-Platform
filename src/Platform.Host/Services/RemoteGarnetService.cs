using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace ScalaAPI.Host.Services;

public interface IGarnetService
{
    void Set(string key, string value, TimeSpan? ttl = null);
    string? Get(string key);
    void Delete(string key);
    long Increment(string key);
    bool Ping();
}

/// <summary>
/// Small RESP client for the external Garnet service. The service owns the
/// cache; this process must never provide a fallback in-memory implementation.
/// </summary>
public sealed class RemoteGarnetService : IGarnetService, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _password;
    private readonly int _timeoutMs;
    private readonly bool _useTls;
    private readonly string _serverName;
    private readonly object _gate = new();
    private TcpClient? _client;
    private Stream? _stream;

    public RemoteGarnetService(IConfiguration configuration)
    {
        _host = configuration["Garnet:Host"] ?? "garnet";
        _port = configuration.GetValue("Garnet:Port", 6379);
        _password = ReadPassword(configuration);
        _timeoutMs = Math.Max(250, configuration.GetValue("Garnet:TimeoutMs", 2000));
        _useTls = configuration.GetValue("Garnet:UseTls", false);
        _serverName = configuration["Garnet:ServerName"] ?? _host;
    }

    public void Set(string key, string value, TimeSpan? ttl = null)
    {
        var args = new List<string> { "SET", key, value };
        if (ttl is { } duration)
        {
            args.Add("PX");
            args.Add(Math.Max(1, (long)duration.TotalMilliseconds).ToString());
        }

        ExpectSimple(Execute(args), "OK");
    }

    public string? Get(string key)
    {
        var reply = Execute(["GET", key]);
        return reply.Kind == ReplyKind.Null ? null : reply.Text;
    }

    public void Delete(string key)
    {
        _ = Execute(["DEL", key]);
    }

    public long Increment(string key)
    {
        var reply = Execute(["INCR", key]);
        if (reply.Kind != ReplyKind.Integer)
            throw new InvalidOperationException("Garnet returned a non-integer INCR response");
        return reply.Integer;
    }

    public bool Ping()
    {
        try
        {
            var reply = Execute(["PING"]);
            return reply.Kind == ReplyKind.Simple && reply.Text == "PONG";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            Disconnect();
    }

    private GarnetReply Execute(IReadOnlyList<string> args)
    {
        lock (_gate)
        {
            try
            {
                EnsureConnected();
                WriteCommand(args);
                return ReadReply();
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
    }

    private void EnsureConnected()
    {
        if (_stream is not null && _client?.Connected == true)
            return;

        Disconnect();
        var client = new TcpClient();
        if (!client.ConnectAsync(_host, _port).Wait(_timeoutMs))
        {
            client.Dispose();
            throw new TimeoutException(
                $"Timed out connecting to Garnet at {_host}:{_port}");
        }
        client.ReceiveTimeout = _timeoutMs;
        client.SendTimeout = _timeoutMs;
        _client = client;
        var networkStream = client.GetStream();
        if (_useTls)
        {
            var tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
            using var timeout = new CancellationTokenSource(_timeoutMs);
            tlsStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _serverName,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeout.Token).GetAwaiter().GetResult();
            _stream = tlsStream;
        }
        else
        {
            _stream = networkStream;
        }

        if (!string.IsNullOrEmpty(_password))
            ExpectSimple(ExecuteConnected(["AUTH", _password]), "OK");
    }

    private GarnetReply ExecuteConnected(IReadOnlyList<string> args)
    {
        WriteCommand(args);
        return ReadReply();
    }

    private void WriteCommand(IReadOnlyList<string> args)
    {
        if (_stream is null)
            throw new InvalidOperationException("Garnet connection is not open");

        using var buffer = new MemoryStream();
        WriteAscii(buffer, $"*{args.Count}\r\n");
        foreach (var arg in args)
        {
            var bytes = Encoding.UTF8.GetBytes(arg);
            WriteAscii(buffer, $"${bytes.Length}\r\n");
            buffer.Write(bytes);
            WriteAscii(buffer, "\r\n");
        }

        _stream.Write(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        _stream.Flush();
    }

    private GarnetReply ReadReply()
    {
        if (_stream is null)
            throw new InvalidOperationException("Garnet connection is not open");

        var type = ReadByte();
        return type switch
        {
            (byte)'+' => new GarnetReply(ReplyKind.Simple, ReadLine(), 0),
            (byte)':' => new GarnetReply(ReplyKind.Integer, null, long.Parse(ReadLine())),
            (byte)'$' => ReadBulk(),
            (byte)'-' => throw new InvalidOperationException($"Garnet error: {ReadLine()}"),
            _ => throw new InvalidOperationException($"Unsupported Garnet RESP reply: 0x{type:X2}")
        };
    }

    private GarnetReply ReadBulk()
    {
        var length = int.Parse(ReadLine());
        if (length < 0)
            return new GarnetReply(ReplyKind.Null, null, 0);

        var bytes = ReadExact(length);
        ExpectCrlf();
        return new GarnetReply(ReplyKind.Bulk, Encoding.UTF8.GetString(bytes), 0);
    }

    private byte ReadByte()
    {
        var value = _stream!.ReadByte();
        if (value < 0)
            throw new EndOfStreamException("Garnet closed the connection");
        return (byte)value;
    }

    private string ReadLine()
    {
        using var line = new MemoryStream();
        while (true)
        {
            var value = ReadByte();
            if (value == '\r')
            {
                if (ReadByte() != '\n')
                    throw new InvalidDataException("Invalid Garnet RESP line ending");
                return Encoding.UTF8.GetString(line.ToArray());
            }
            line.WriteByte(value);
        }
    }

    private byte[] ReadExact(int length)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = _stream!.Read(result, offset, length - offset);
            if (read == 0)
                throw new EndOfStreamException("Garnet closed the connection");
            offset += read;
        }
        return result;
    }

    private void ExpectCrlf()
    {
        if (ReadByte() != '\r' || ReadByte() != '\n')
            throw new InvalidDataException("Invalid Garnet RESP bulk ending");
    }

    private static void ExpectSimple(GarnetReply reply, string expected)
    {
        if (reply.Kind != ReplyKind.Simple || reply.Text != expected)
            throw new InvalidOperationException($"Unexpected Garnet response: {reply.Text}");
    }

    private void Disconnect()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    private static string? ReadPassword(IConfiguration configuration)
    {
        var path = configuration["Garnet:PasswordFile"];
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return File.ReadAllText(path).Trim();
        return configuration["Garnet:Password"];
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
    }

    private readonly record struct GarnetReply(ReplyKind Kind, string? Text, long Integer);

    private enum ReplyKind
    {
        Simple,
        Integer,
        Bulk,
        Null
    }
}
