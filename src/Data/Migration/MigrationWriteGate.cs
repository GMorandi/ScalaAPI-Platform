namespace Sub2Api.Data.Migration;

public sealed class MigrationWriteRejectedException(string message) : InvalidOperationException(message);

/// <summary>
/// Central authority check for target-side business writes. Reads, migration
/// control operations, and CDC application are intentionally separate paths.
/// `target_canary` is observation-only until a scoped canary writer and matching
/// source-side fence exist; allowing all target writes there would create two
/// business writers while Sub2API remains primary.
/// </summary>
public sealed class MigrationWriteGate(MigrationFenceStore fence)
{
    private long _rejections;

    public long RejectionCount => Interlocked.Read(ref _rejections);

    public async Task AssertPlatformPrimaryAsync(CancellationToken ct = default)
    {
        var current = await fence.GetAsync(ct);
        if (!string.Equals(current.WritePrimary, "platform", StringComparison.Ordinal)
            || current.Mode is not "target_primary")
        {
            Interlocked.Increment(ref _rejections);
            throw new MigrationWriteRejectedException(
                $"platform business write rejected by migration fence: {current.WritePrimary}@{current.Epoch}/{current.Mode}");
        }
    }
}
