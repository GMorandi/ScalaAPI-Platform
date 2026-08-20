using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ScalaAPI.Host.Services;

public sealed class BlobChunkRpc
{
    public string BlobId { get; init; } = "";
    public uint Seq { get; init; }
    public uint Index { get; init; }
    public byte[] Data { get; init; } = [];
    public bool IsLast { get; init; }
}

public sealed class BlobChunkAckResult
{
    public bool Accepted { get; init; }
    public string ErrorCode { get; init; } = "";
    public string BlobId { get; init; } = "";
    public string Digest { get; init; } = "";
    public ulong TotalBytes { get; init; }
}

public sealed class DispatchBlobStore : IDisposable
{
    private readonly ConcurrentDictionary<string, BlobEntry> _blobs = new();
    private readonly PeriodicTimer _cleanup;
    private readonly TimeSpan _ttl;
    private readonly long _maxTotalBytes;
    private long _currentTotalBytes;
    private readonly CancellationTokenSource _cts = new();

    public DispatchBlobStore(TimeSpan ttl, long maxTotalBytes)
    {
        _ttl = ttl;
        _maxTotalBytes = maxTotalBytes;
        _cleanup = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _ = RunCleanupAsync();
    }

    private async Task RunCleanupAsync()
    {
        try
        {
            while (await _cleanup.WaitForNextTickAsync(_cts.Token))
                EvictExpired();
        }
        catch (OperationCanceledException) { }
    }

    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, entry) in _blobs)
        {
            if (now - entry.CreatedAt > _ttl)
            {
                if (_blobs.TryRemove(key, out var removed))
                    Interlocked.Add(ref _currentTotalBytes, -removed.TotalBytes);
            }
        }
    }

    public BlobChunkAckResult IngestChunk(BlobChunkRpc chunk)
    {
        if (string.IsNullOrEmpty(chunk.BlobId))
            return new BlobChunkAckResult { ErrorCode = "invalid_blob_id" };

        var entry = _blobs.GetOrAdd(chunk.BlobId, _ => new BlobEntry
        {
            CreatedAt = DateTime.UtcNow,
            Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
        });

        if (entry.Completed)
            return new BlobChunkAckResult
            {
                Accepted = true,
                BlobId = chunk.BlobId,
                Digest = entry.Digest ?? "",
                TotalBytes = (ulong)entry.TotalBytes,
            };

        if (entry.NextIndex != chunk.Index)
            return new BlobChunkAckResult { ErrorCode = "out_of_order" };

        var newDataSize = entry.TotalBytes + chunk.Data.Length;
        if (Interlocked.Read(ref _currentTotalBytes) + chunk.Data.Length > _maxTotalBytes)
            return new BlobChunkAckResult { ErrorCode = "blobStoreFull" };

        entry.Hasher.AppendData(chunk.Data);
        entry.Chunks.Add(chunk.Data);
        entry.TotalBytes = newDataSize;
        entry.NextIndex = chunk.Index + 1;
        Interlocked.Add(ref _currentTotalBytes, chunk.Data.Length);

        if (chunk.IsLast)
        {
            entry.Digest = Convert.ToHexString(entry.Hasher.GetHashAndReset()).ToLowerInvariant();
            entry.Completed = true;
        }

        return new BlobChunkAckResult
        {
            Accepted = true,
            BlobId = chunk.BlobId,
            Digest = entry.Digest ?? "",
            TotalBytes = (ulong)entry.TotalBytes,
        };
    }

    public byte[]? GetBlob(string blobId)
    {
        if (!_blobs.TryGetValue(blobId, out var entry) || !entry.Completed)
            return null;
        var result = new byte[entry.TotalBytes];
        var offset = 0;
        foreach (var chunk in entry.Chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cleanup.Dispose();
    }

    private sealed class BlobEntry
    {
        public DateTime CreatedAt;
        public IncrementalHash Hasher = null!;
        public List<byte[]> Chunks = [];
        public long TotalBytes;
        public uint NextIndex;
        public bool Completed;
        public string? Digest;
    }
}
