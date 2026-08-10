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
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand(
            $"SELECT pg_advisory_xact_lock({ContentPolicyPropagationLock.Key})",
            connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using var command = new NpgsqlCommand("""
            SELECT revision
            FROM content_policy_state
            WHERE id = 1
            """, connection, transaction);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is not long revision || revision < 1)
            throw new InvalidOperationException(
                "Authoritative content policy revision is missing or invalid");

        var invalidationVersion = garnet.PublishContentPolicyRevision(revision);
        await transaction.CommitAsync(ct);
        return new GarnetPolicyRevisionRebuildResult(revision, invalidationVersion);
    }
}
