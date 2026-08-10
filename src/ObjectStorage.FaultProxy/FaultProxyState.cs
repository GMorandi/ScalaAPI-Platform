namespace ScalaAPI.ObjectStorage.FaultProxy;

public static class ObjectStorageFaultModes
{
    public const string TruncateRequest = "truncate_request";
    public const string DropResponse = "drop_response";
}

public sealed record FaultArmRequest(
    string Mode, string Method, string PathContains, int RequestBodyBytes = 0);

public sealed record ArmedObjectStorageFault(
    string Mode, string Method, string PathContains, int RequestBodyBytes);

public sealed record ObjectStorageProxyEvidence(
    long Sequence, DateTimeOffset ObservedAt, string Method, string Path,
    string Action, long RequestBodyBytes, int? UpstreamStatus, string Error);

public sealed record ObjectStorageFaultSnapshot(
    ArmedObjectStorageFault? Armed, IReadOnlyList<ObjectStorageProxyEvidence> Events);

public sealed class FaultProxyState
{
    private const int EvidenceLimit = 512;
    private readonly object _gate = new();
    private readonly Queue<ObjectStorageProxyEvidence> _events = new();
    private ArmedObjectStorageFault? _armed;
    private long _sequence;

    public ArmedObjectStorageFault Arm(FaultArmRequest request)
    {
        var mode = request.Mode?.Trim().ToLowerInvariant() ?? "";
        if (mode is not (ObjectStorageFaultModes.TruncateRequest
            or ObjectStorageFaultModes.DropResponse))
            throw new ArgumentException("mode must be truncate_request or drop_response");

        var method = request.Method?.Trim().ToUpperInvariant() ?? "";
        if (method.Length is < 3 or > 16 || method.Any(ch => !char.IsAsciiLetter(ch)))
            throw new ArgumentException("method must be an HTTP token");

        var path = request.PathContains?.Trim() ?? "";
        if (path.Length is < 1 or > 512 || path.Any(char.IsControl))
            throw new ArgumentException("pathContains must contain 1 to 512 safe characters");

        var requestBodyBytes = mode == ObjectStorageFaultModes.TruncateRequest
            ? request.RequestBodyBytes : 0;
        if (mode == ObjectStorageFaultModes.TruncateRequest
            && requestBodyBytes is < 1 or > 1024 * 1024)
            throw new ArgumentException(
                "requestBodyBytes must be between 1 and 1048576 for truncate_request");

        var armed = new ArmedObjectStorageFault(mode, method, path, requestBodyBytes);
        lock (_gate)
        {
            if (_armed is not null)
                throw new InvalidOperationException("an object-storage fault is already armed");
            _armed = armed;
        }
        return armed;
    }

    public ArmedObjectStorageFault? TryConsume(string method, string path)
    {
        lock (_gate)
        {
            if (_armed is null
                || !string.Equals(_armed.Method, method, StringComparison.Ordinal)
                || !path.Contains(_armed.PathContains, StringComparison.Ordinal))
                return null;
            var result = _armed;
            _armed = null;
            return result;
        }
    }

    public void Record(string method, string path, string action,
        long requestBodyBytes, int? upstreamStatus = null, string error = "")
    {
        lock (_gate)
        {
            _events.Enqueue(new ObjectStorageProxyEvidence(
                ++_sequence, DateTimeOffset.UtcNow, method, path, action,
                requestBodyBytes, upstreamStatus,
                error.Length > 512 ? error[..512] : error));
            while (_events.Count > EvidenceLimit) _events.Dequeue();
        }
    }

    public ObjectStorageFaultSnapshot Snapshot()
    {
        lock (_gate) return new(_armed, _events.ToArray());
    }

    public void Clear()
    {
        lock (_gate)
        {
            _armed = null;
            _events.Clear();
        }
    }
}
