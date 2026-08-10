using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using ScalaAPI.Host.Services;

namespace ScalaAPI.Host.Tests;

public sealed class RemoteGarnetTlsTests
{
    [Fact]
    public async Task UsesConfiguredCaAndServerNameForTlsResp()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = CreateCertificate(rsa, "garnet.test");
        var caPath = await WriteCertificateAsync(certificate);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunServerAsync(listener, certificate, expectAuth: true);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Garnet:Host"] = "127.0.0.1",
                    ["Garnet:Port"] = port.ToString(),
                    ["Garnet:Password"] = "secret",
                    ["Garnet:UseTls"] = "true",
                    ["Garnet:ServerName"] = "garnet.test",
                    ["Garnet:CaCertificatePath"] = caPath,
                    ["Garnet:TimeoutMs"] = "2000",
                }).Build();

            using var client = new RemoteGarnetService(configuration);
            Assert.True(client.Ping());
            await server;
        }
        finally
        {
            listener.Stop();
            File.Delete(caPath);
        }
    }

    [Fact]
    public async Task RejectsWrongServerNameEvenWhenCaIsTrusted()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = CreateCertificate(rsa, "garnet.test");
        var caPath = await WriteCertificateAsync(certificate);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunServerAsync(listener, certificate, expectAuth: false);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Garnet:Host"] = "127.0.0.1",
                    ["Garnet:Port"] = port.ToString(),
                    ["Garnet:UseTls"] = "true",
                    ["Garnet:ServerName"] = "wrong.test",
                    ["Garnet:CaCertificatePath"] = caPath,
                }).Build();

            using var client = new RemoteGarnetService(configuration);
            Assert.False(client.Ping());
            await server;
        }
        finally
        {
            listener.Stop();
            File.Delete(caPath);
        }
    }

    [Fact]
    public void RejectsCaPathWhenTlsIsDisabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Garnet:UseTls"] = "false",
                ["Garnet:CaCertificatePath"] = Path.GetTempFileName(),
            }).Build();
        try
        {
            Assert.Throws<InvalidOperationException>(() => new RemoteGarnetService(configuration));
        }
        finally
        {
            File.Delete(configuration["Garnet:CaCertificatePath"]!);
        }
    }

    private static async Task RunServerAsync(TcpListener listener,
        X509Certificate2 certificate, bool expectAuth)
    {
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            if (!expectAuth) return;
            Assert.Equal(["AUTH", "secret"], await ReadCommandAsync(tls));
            await WriteAsync(tls, "+OK\r\n");
            Assert.Equal(["PING"], await ReadCommandAsync(tls));
            await WriteAsync(tls, "+PONG\r\n");
        }
        catch (AuthenticationException) when (!expectAuth)
        {
        }
        catch (IOException) when (!expectAuth)
        {
        }
    }

    private static async Task<string[]> ReadCommandAsync(Stream stream)
    {
        var header = await ReadLineAsync(stream);
        Assert.StartsWith("*", header);
        var count = int.Parse(header[1..]);
        var values = new string[count];
        for (var index = 0; index < count; index++)
        {
            var length = int.Parse((await ReadLineAsync(stream))[1..]);
            var bytes = await ReadExactAsync(stream, length);
            Assert.Equal("\r\n", await ReadExactTextAsync(stream, 2));
            values[index] = Encoding.UTF8.GetString(bytes);
        }
        return values;
    }

    private static async Task<string> ReadLineAsync(Stream stream) =>
        await ReadUntilCrlfAsync(stream);

    private static async Task<string> ReadUntilCrlfAsync(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            if (value == '\r')
            {
                if (stream.ReadByte() != '\n') throw new InvalidDataException();
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add((byte)value);
            await Task.Yield();
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(offset, length - offset));
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }

    private static async Task<string> ReadExactTextAsync(Stream stream, int length) =>
        Encoding.ASCII.GetString(await ReadExactAsync(stream, length));

    private static async Task WriteAsync(Stream stream, string value) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(value));

    private static X509Certificate2 CreateCertificate(RSA rsa, string dnsName)
    {
        var request = new CertificateRequest($"CN={dnsName}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
    }

    private static async Task<string> WriteCertificateAsync(X509Certificate2 certificate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scalaapi-garnet-{Guid.NewGuid():N}.pem");
        var encoded = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
        var pem = new StringBuilder("-----BEGIN CERTIFICATE-----\n");
        for (var index = 0; index < encoded.Length; index += 64)
            pem.AppendLine(encoded.Substring(index, Math.Min(64, encoded.Length - index)));
        pem.AppendLine("-----END CERTIFICATE-----");
        await File.WriteAllTextAsync(path, pem.ToString());
        return path;
    }
}
