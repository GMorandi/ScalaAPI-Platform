using System.Reflection;
using Capnp;
using CapnpGen;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public class CapnpResponseSerializationTests
{
    [Fact]
    public void RejectedResponseIsAttachedToTheMessageRoot()
    {
        var response = Serialize(DispatchResult.Rejected("invalidKey", "invalid"));

        Assert.Equal((byte)0x81, response[0]);
        var decoded = Deserialize(response);
        Assert.Equal((ushort)3, decoded.ProtocolVersion);
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

    [Fact]
    public void PricingUsesFixedScaleIntegerWireFields()
    {
        var response = Serialize(DispatchResult.Ok(new UpstreamTargetResult
        {
            AccountId = 11,
            RateMultiplier = 1.23456789m,
            HoldAmount = 2.5m,
            HoldHandle = "hold-1",
            LeaseToken = "lease-precision",
        }));

        var decoded = Deserialize(response);
        Assert.Equal(123456789L, decoded.Upstream.Billing.RateMultiplier);
        Assert.Equal(250000000L, decoded.Upstream.Billing.HoldAmount);
    }

    [Fact]
    public void ResponseCarriesTheCurrentProtocolVersion()
    {
        var response = Serialize(DispatchResult.Rejected("invalidKey", "invalid") with
        {
            ProtocolVersion = 3
        });

        Assert.Equal((ushort)3, Deserialize(response).ProtocolVersion);
    }

    [Fact]
    public void PricingUnavailableHasDedicatedRejectCode()
    {
        var decoded = Deserialize(Serialize(DispatchResult.Rejected(
            "pricingUnavailable", "missing price")));

        Assert.Equal(RejectInfo.RejectCode.pricingUnavailable, decoded.Reject.Code);
    }

    [Fact]
    public void PlatformUnavailableHasDedicatedRetryableRejectCode()
    {
        var decoded = Deserialize(Serialize(DispatchResult.Rejected(
            "platformUnavailable", "retry may be safe")));

        Assert.Equal(RejectInfo.RejectCode.platformUnavailable, decoded.Reject.Code);
    }

    [Fact]
    public void IdempotentReplayPreservesResponsePayload()
    {
        var decoded = Deserialize(Serialize(DispatchResult.Replay(
            200, "application/json", "{\"ok\":true}")));

        Assert.Equal(DispatchResponse.Outcome.rejected, decoded.TheOutcome);
        Assert.Equal(RejectInfo.RejectCode.idempotencyReplay, decoded.Reject.Code);
        Assert.Equal(200, decoded.ReplayStatusCode);
        Assert.Equal("application/json", decoded.ReplayContentType);
        Assert.Equal("{\"ok\":true}", decoded.ReplayBody);
    }

    [Fact]
    public void MediaOperationResponsePreservesDurableTaskFields()
    {
        var result = new MediaOperationRpcResult(true, 202, "med_1",
            "images_generations_async", "running", 35, "provider_task_1",
            "{\"status\":\"running\"}", "", "application/json", "", "");
        var method = typeof(CapnpRpcHostedService).GetMethod(
            "SerializeMediaOperationResponse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var response = Assert.IsType<byte[]>(method.Invoke(null, [result]));
        Assert.Equal((byte)0x85, response[0]);
        var decoded = DeserializeRoot<MediaOperationResponse>(response);
        Assert.True(decoded.Accepted);
        Assert.Equal(202, decoded.StatusCode);
        Assert.Equal("med_1", decoded.OperationId);
        Assert.Equal("provider_task_1", decoded.UpstreamTaskId);
        Assert.Equal(35, decoded.Progress);
    }

    [Fact]
    public void UpstreamPathPreservesOnlyValidatedQueryStrings()
    {
        var request = new ScalaAPI.Host.Services.DispatchRequest(
            "hash", "model", "session", "127.0.0.1", "req", [], 0,
            "models", null, false, Operation: "models", HttpMethod: "GET",
            RequestPath: "/v1/models", Capability: "models",
            RequestQuery: "?limit=20&after=item%2F1");
        Assert.Equal("/v1/models?limit=20&after=item%2F1",
            ResolveUpstreamPath("openai", request, "model"));

        Assert.Equal("/v1/models", ResolveUpstreamPath("openai",
            request with { RequestQuery = "?next=x\r\nInjected: yes" }, "model"));
        Assert.Equal("/v1/models", ResolveUpstreamPath("openai",
            request with { RequestQuery = "?next=%2" }, "model"));
    }

    [Fact]
    public void GeminiStreamingQueryMergesWithRequiredSseFlag()
    {
        var request = new ScalaAPI.Host.Services.DispatchRequest(
            "hash", "gemini-2.5-pro", "session", "127.0.0.1", "req", [], 0,
            "gemini", null, true, Operation: "streamGenerateContent",
            RequestPath: "/v1beta/models/gemini-2.5-pro:streamGenerateContent",
            Capability: "gemini_generate", RequestQuery: "?page_token=a%2Fb");
        Assert.Equal(
            "/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse&page_token=a%2Fb",
            ResolveUpstreamPath("gemini", request, "gemini-2.5-pro"));
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

    private static T DeserializeRoot<T>(byte[] response) where T : class, ICapnpSerializable
    {
        using var stream = new MemoryStream(response, 1, response.Length - 1);
        using var reader = new BinaryReader(stream);
        var frame = Framing.ReadWireFrame(reader);
        using var state = DeserializerState.CreateRoot(frame);
        return CapnpSerializable.Create<T>(state)!;
    }

    private static string ResolveUpstreamPath(string platform,
        ScalaAPI.Host.Services.DispatchRequest request, string mappedModel)
    {
        var method = typeof(DispatchService).GetMethod(
            "ResolveUpstreamPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [platform, request, mappedModel]));
    }
}
