using Npgsql;

namespace ScalaAPI.Host.Services;

public sealed record GarnetPolicyRevisionRebuildResult(
    long PolicyRevision,
    long InvalidationVersion);

/// <summary>
/// Restores the disposable content-policy projection from PostgreSQL after a
/// Garnet flush or replacement. Business decisions continue to read the
/// authoritative PostgreSQL rule/state transaction.
/// </summary>
public sealed class GarnetPolicyRevisionRebuildService(
    NpgsqlDataSource dataSource,
    GarnetWriteThroughService garnet)
{
    public async Task<GarnetPolicyRevisionRebuildResult> RebuildAsync(
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT revision
            FROM content_policy_state
            WHERE id = 1
            """);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is not long revision || revision < 1)
            throw new InvalidOperationException(
                "Authoritative content policy revision is missing or invalid");

        var invalidationVersion = garnet.PublishContentPolicyRevision(revision);
        return new GarnetPolicyRevisionRebuildResult(revision, invalidationVersion);
    }
}
