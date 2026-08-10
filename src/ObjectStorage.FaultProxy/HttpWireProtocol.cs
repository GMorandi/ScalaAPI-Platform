using System.Globalization;
using System.Text;

namespace ScalaAPI.ObjectStorage.FaultProxy;

public sealed record HttpRequestHead(
    byte[] Bytes, string Method, string Path, long ContentLength);

public static class HttpWireProtocol
{
    private const int MaxHeaderBytes = 64 * 1024;

    public static async Task<byte[]?> ReadHeadAsync(Stream stream,
        CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var matched = 0;
        while (buffer.Length < MaxHeaderBytes)
        {
            var value = new byte[1];
            var read = await stream.ReadAsync(value, ct);
            if (read == 0)
            {
                if (buffer.Length == 0) return null;
                throw new EndOfStreamException("HTTP headers ended before CRLF CRLF");
            }
            buffer.WriteByte(value[0]);
            matched = (matched, value[0]) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 13) => 1,
                _ => 0,
            };
            if (matched == 4) return buffer.ToArray();
        }
        throw new InvalidDataException("HTTP headers exceed 65536 bytes");
    }

    public static HttpRequestHead ParseRequest(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (request.Length != 3 || !request[2].StartsWith("HTTP/", StringComparison.Ordinal))
            throw new InvalidDataException("invalid HTTP request line");
        var target = request[1];
        var query = target.IndexOf('?');
        var path = query < 0 ? target : target[..query];
        if (!path.StartsWith('/')) throw new InvalidDataException("invalid HTTP request target");

        var contentLength = 0L;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            if (name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)
                && !line[(separator + 1)..].Trim().Equals("identity",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("chunked requests are not supported by the fault proxy");
            if (!name.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!long.TryParse(line[(separator + 1)..].Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out contentLength)
                || contentLength < 0 || contentLength > 512L * 1024 * 1024)
                throw new InvalidDataException("invalid or oversized HTTP Content-Length");
        }
        return new(bytes, request[0].ToUpperInvariant(), path, contentLength);
    }

    public static int ParseResponseStatus(byte[] bytes)
    {
        var lineEnd = Array.IndexOf(bytes, (byte)'\r');
        if (lineEnd < 0) throw new InvalidDataException("invalid HTTP response line");
        var parts = Encoding.Latin1.GetString(bytes, 0, lineEnd)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out var status) || status is < 100 or > 999)
            throw new InvalidDataException("invalid HTTP response status");
        return status;
    }

    public static byte[] WithConnectionClose(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            throw new InvalidDataException("HTTP header terminator is missing");
        var lines = text[..^4].Split("\r\n", StringSplitOptions.None)
            .Where(line => !line.StartsWith("Connection:",
                    StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("Proxy-Connection:",
                    StringComparison.OrdinalIgnoreCase));
        return Encoding.Latin1.GetBytes(
            string.Join("\r\n", lines) + "\r\nConnection: close\r\n\r\n");
    }
}
