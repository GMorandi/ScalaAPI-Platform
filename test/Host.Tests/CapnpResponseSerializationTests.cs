using System.Reflection;
using Capnp;
using CapnpGen;
using Sub2Api.Host.Services;
using Xunit;

namespace Sub2Api.Host.Tests;

public class CapnpResponseSerializationTests
{
    [Fact]
    public void RejectedResponseIsAttachedToTheMessageRoot()
    {
        var response = Serialize(DispatchResult.Rejected("invalidKey", "invalid"));

        Assert.Equal((byte)0x81, response[0]);
        var decoded = Deserialize(response);
        Assert.Equal((ushort)2, decoded.ProtocolVersion);
        Assert.Equal(DispatchResponse.Outcome.rejected, decoded.TheOutcome);
        Assert.Equal("invalid", decoded.Reject.Message);
    }

    [Fact]
    public void SuccessfulResponsePreservesLeaseAndServerIdentity()
    {
        var response = Serialize(DispatchResult.Ok(new UpstreamTargetResult
        {
            AccountId = 11,
            ApiKeyId = 22,
            UserId = 33,
            GroupId = 44,
            Platform = "openai",
            BaseUrl = "https://upstream.example",
            UpstreamPath = "/v1/chat/completions",
            MappedModel = "model-b",
            LeaseToken = "lease-1",
            AuthVersion = 7,
            AuthHeaders = new() { ["Authorization"] = "Bearer test" },
        }));

        var decoded = Deserialize(response);
        Assert.Equal(DispatchResponse.Outcome.ok, decoded.TheOutcome);
        Assert.Equal("lease-1", decoded.LeaseToken);
        Assert.Equal(22, decoded.Auth.ApiKeyId);
        Assert.Equal(11, decoded.Upstream.AccountId);
        Assert.Equal("/v1/chat/completions", decoded.Upstream.UpstreamPath);
    }

    private static byte[] Serialize(DispatchResult result)
    {
        var method = typeof(CapnpRpcHostedService).GetMethod(
            "BuildDispatchResponse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<byte[]>(method.Invoke(null, [result]));
    }

    private static DispatchResponse Deserialize(byte[] response)
    {
        using var stream = new MemoryStream(response, 1, response.Length - 1);
        using var reader = new BinaryReader(stream);
        var frame = Framing.ReadWireFrame(reader);
        using var state = DeserializerState.CreateRoot(frame);
        return CapnpSerializable.Create<DispatchResponse>(state)!;
    }
}
