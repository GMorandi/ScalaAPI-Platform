using System.Text;
using ScalaAPI.ObjectStorage.FaultProxy;

namespace ScalaAPI.Host.Tests;

public sealed class FaultProxyStateTests
{
    [Fact]
    public void ArmedFaultIsValidatedMatchedAndConsumedOnce()
    {
        var state = new FaultProxyState();
        var armed = state.Arm(new(ObjectStorageFaultModes.TruncateRequest,
            "put", "/items/", 16));

        Assert.Equal("PUT", armed.Method);
        Assert.Null(state.TryConsume("HEAD", "/bucket/media/op/items/one.png"));
        Assert.Null(state.TryConsume("PUT", "/bucket/media/op.zip"));
        Assert.Equal(armed,
            state.TryConsume("PUT", "/bucket/media/op/items/one.png"));
        Assert.Null(state.TryConsume("PUT", "/bucket/media/op/items/one.png"));
    }

    [Fact]
    public void InvalidAndOverlappingFaultsAreRejected()
    {
        var state = new FaultProxyState();
        Assert.Throws<ArgumentException>(() => state.Arm(new(
            "unknown", "PUT", "/media/", 1)));
        Assert.Throws<ArgumentException>(() => state.Arm(new(
            ObjectStorageFaultModes.TruncateRequest, "PUT", "/media/", 0)));
        state.Arm(new(ObjectStorageFaultModes.DropResponse, "PUT", "/media/"));
        Assert.Throws<InvalidOperationException>(() => state.Arm(new(
            ObjectStorageFaultModes.DropResponse, "PUT", "/other/")));
    }

    [Fact]
    public void EvidenceIsBoundedAndClearRemovesFaultAndHistory()
    {
        var state = new FaultProxyState();
        state.Arm(new(ObjectStorageFaultModes.DropResponse, "PUT", "/media/"));
        for (var index = 0; index < 520; index++)
            state.Record("PUT", $"/media/{index}", "pass", index, 200);

        var snapshot = state.Snapshot();
        Assert.Equal(512, snapshot.Events.Count);
        Assert.Equal(9, snapshot.Events[0].Sequence);
        Assert.NotNull(snapshot.Armed);

        state.Clear();
        Assert.Null(state.Snapshot().Armed);
        Assert.Empty(state.Snapshot().Events);
    }

    [Fact]
    public void WireParserPreservesSignedHeadersAndForcesConnectionClose()
    {
        var original = Encoding.Latin1.GetBytes(
            "PUT /bucket/media/op/items/one.png?part=1 HTTP/1.1\r\n" +
            "Host: proxy:9000\r\n" +
            "X-Amz-Date: 20260810T010203Z\r\n" +
            "Content-Length: 67\r\n" +
            "Connection: keep-alive\r\n\r\n");

        var parsed = HttpWireProtocol.ParseRequest(original);
        var rewritten = Encoding.Latin1.GetString(
            HttpWireProtocol.WithConnectionClose(original));

        Assert.Equal("PUT", parsed.Method);
        Assert.Equal("/bucket/media/op/items/one.png", parsed.Path);
        Assert.Equal(67, parsed.ContentLength);
        Assert.Contains("Host: proxy:9000\r\n", rewritten);
        Assert.Contains("X-Amz-Date: 20260810T010203Z\r\n", rewritten);
        Assert.DoesNotContain("keep-alive", rewritten);
        Assert.EndsWith("Connection: close\r\n\r\n", rewritten);
    }
}
