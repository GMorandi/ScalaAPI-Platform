using System.Buffers.Binary;
using System.Net.Sockets;
using System.Reflection;
using Capnp;
using CapnpGen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class CapnpRpcFramingTests
{
    [Fact]
    public async Task OversizeInboundFrameGetsNonRetryableErrorAndConnectionStaysUsable()
    {
        var (service, socketPath) = await StartServiceAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
            using var stream = new NetworkStream(client);

            // One byte over the 8 MiB cap: method byte + filler payload.
            var payload = new byte[8 * 1024 * 1024 + 1];
            payload[0] = 2; // reportUsage
            await WriteFrameAsync(stream, payload, timeout.Token);

            var rejected = await ReadFrameAsync(stream, timeout.Token);
            Assert.Equal((byte)0x82, rejected[0]);
            var ack = DeserializeRoot<CapnpGen.WriteAck>(rejected);
            Assert.False(ack.Accepted);
            Assert.False(ack.Retryable);
            Assert.Equal("frame_too_large", ack.ErrorCode);

            // The connection must stay usable: a well-formed frame is still answered.
            await WriteFrameAsync(stream, [99], timeout.Token);
            var followUp = await ReadFrameAsync(stream, timeout.Token);
            Assert.Equal((byte)0x81, followUp[0]);
            var reject = DeserializeRoot<DispatchResponse>(followUp);
            Assert.Equal(DispatchResponse.Outcome.rejected, reject.TheOutcome);
            Assert.Equal("unknown method", reject.Reject.Message);
        }
        finally
        {
            await StopServiceAsync(service, socketPath);
        }
    }

    [Fact]
    public async Task UsageReportFrameAboveOneMiBRoundTripsThroughTheFramingLayer()
    {
        var (service, socketPath) = await StartServiceAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
            using var stream = new NetworkStream(client);

            var frame = BuildUsageReportFrame(new string('x', 1536 * 1024));
            Assert.True(frame.Length - 4 > 1024 * 1024); // exceeds the old 1 MiB cap
            await stream.WriteAsync(frame, timeout.Token);

            var response = await ReadFrameAsync(stream, timeout.Token);
            Assert.Equal((byte)0x82, response[0]);
            var ack = DeserializeRoot<CapnpGen.WriteAck>(response);
            Assert.False(ack.Accepted);
            // The frame cleared the raised cap and reached processing, where the
            // test's dependency-free DispatchService fails with a transient error.
            Assert.Equal("platform_error", ack.ErrorCode);
            Assert.True(ack.Retryable);
        }
        finally
        {
            await StopServiceAsync(service, socketPath);
        }
    }

    [Theory]
    [InlineData((byte)2, (byte)0x82)]
    [InlineData((byte)3, (byte)0x83)]
    [InlineData((byte)4, (byte)0x84)]
    [InlineData((byte)6, (byte)0x86)]
    public void OversizeErrorResponseForAckMethodsIsNonRetryable(byte method, byte responseMethod)
    {
        var response = BuildOversizeFrameError(method);
        Assert.Equal(responseMethod, response[0]);
        var ack = DeserializeRoot<CapnpGen.WriteAck>(response);
        Assert.False(ack.Accepted);
        Assert.False(ack.Retryable);
        Assert.Equal("frame_too_large", ack.ErrorCode);
    }

    [Fact]
    public void OversizeErrorResponseForContentPolicyFailsClosedNonRetryable()
    {
        var response = BuildOversizeFrameError(7);
        Assert.Equal((byte)0x87, response[0]);
        var decoded = DeserializeRoot<ContentPolicyResponse>(response);
        Assert.False(decoded.Evaluated);
        Assert.False(decoded.Allowed);
        Assert.False(decoded.Retryable);
        Assert.Equal("frame_too_large", decoded.ErrorCode);
    }

    [Fact]
    public void OversizeErrorResponseForMediaOperationIsRejected()
    {
        var response = BuildOversizeFrameError(5);
        Assert.Equal((byte)0x85, response[0]);
        var decoded = DeserializeRoot<MediaOperationResponse>(response);
        Assert.False(decoded.Accepted);
        Assert.Equal("frame_too_large", decoded.ErrorCode);
    }

    [Fact]
    public void OversizeErrorResponseForBlobUploadIsRejected()
    {
        var response = BuildOversizeFrameError(8);
        Assert.Equal((byte)0x88, response[0]);
        var decoded = DeserializeRoot<BlobChunkAck>(response);
        Assert.False(decoded.Accepted);
        Assert.Equal("frame_too_large", decoded.ErrorCode);
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)99)]
    public void OversizeErrorResponseForDispatchAndUnknownMethodsRejectsWithoutRetry(byte method)
    {
        var response = BuildOversizeFrameError(method);
        Assert.Equal((byte)0x81, response[0]);
        var decoded = DeserializeRoot<DispatchResponse>(response);
        Assert.Equal(DispatchResponse.Outcome.rejected, decoded.TheOutcome);
        Assert.Equal(RejectInfo.RejectCode.noAccount, decoded.Reject.Code);
    }

    [Fact]
    public void ReplayBodyAboveOneMiBSerializesWithinTheFrameCap()
    {
        var body = new string('x', 1536 * 1024);
        var response = Serialize(DispatchResult.Replay(200, "application/json", body));
        Assert.True(response.Length > 1024 * 1024);
        Assert.True(response.Length <= 8 * 1024 * 1024);
        Assert.Equal(body, DeserializeRoot<DispatchResponse>(response).ReplayBody);
    }

    [Fact]
    public void FrameCapIsEightMiB()
    {
        var field = typeof(CapnpRpcHostedService).GetField(
            "MaxFrameBytes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(8 * 1024 * 1024, Assert.IsType<int>(field.GetValue(null)));
    }

    private static async Task<(CapnpRpcHostedService Service, string SocketPath)> StartServiceAsync()
    {
        var socketPath = Path.Combine(Path.GetTempPath(),
            "scalaapi-rpc-test-" + Guid.NewGuid().ToString("N") + ".sock");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CapnpRpc:SocketPath"] = socketPath,
            })
            .Build();
        // The framing tests never reach a code path that dereferences the
        // dispatch dependencies; only the type must be resolvable.
        var dispatch = new DispatchService(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().Build(), NullLogger<DispatchService>.Instance,
            null!, null!, null!);
        var services = new ServiceCollection()
            .AddSingleton(dispatch)
            .BuildServiceProvider();
        var service = new CapnpRpcHostedService(
            NullLogger<CapnpRpcHostedService>.Instance, services, configuration);
        await service.StartAsync(default);
        return (service, socketPath);
    }

    private static async Task StopServiceAsync(CapnpRpcHostedService service, string socketPath)
    {
        await service.StopAsync(default);
        if (File.Exists(socketPath))
            File.Delete(socketPath);
    }

    private static byte[] BuildUsageReportFrame(string responseBody)
    {
        var message = MessageBuilder.Create();
        var writer = message.BuildRoot<UsageReport.WRITER>();
        writer.LeaseToken = "lease-framing-roundtrip";
        writer.ResponseBody = responseBody;
        using var ms = new MemoryStream();
        ms.WriteByte(2); // reportUsage
        var pump = new FramePump(ms);
        pump.Send(message.Frame);
        pump.Flush();
        var payload = ms.ToArray();
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var hdr = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, (uint)payload.Length);
        await stream.WriteAsync(hdr, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var hdr = new byte[4];
        await ReadExactAsync(stream, hdr, ct);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
        Assert.True(len > 0 && len <= 8 * 1024 * 1024,
            $"response frame length {len} violates the frame cap");
        var payload = new byte[len];
        await ReadExactAsync(stream, payload, ct);
        return payload;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(offset, buf.Length - offset), ct);
            Assert.True(n > 0, "connection closed mid-frame");
            offset += n;
        }
    }

    private static byte[] BuildOversizeFrameError(byte method)
    {
        var reflected = typeof(CapnpRpcHostedService).GetMethod(
            "BuildOversizeFrameErrorResponse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(reflected);
        return Assert.IsType<byte[]>(reflected.Invoke(null, [method]));
    }

    private static byte[] Serialize(DispatchResult result)
    {
        var method = typeof(CapnpRpcHostedService).GetMethod(
            "BuildDispatchResponse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<byte[]>(method.Invoke(null, [result]));
    }

    private static T DeserializeRoot<T>(byte[] response) where T : class, ICapnpSerializable
    {
        using var stream = new MemoryStream(response, 1, response.Length - 1);
        using var reader = new BinaryReader(stream);
        var frame = Framing.ReadWireFrame(reader);
        using var state = DeserializerState.CreateRoot(frame);
        return CapnpSerializable.Create<T>(state)!;
    }
}
