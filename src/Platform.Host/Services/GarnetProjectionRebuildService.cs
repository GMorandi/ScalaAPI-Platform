using System.Text.Json;
using Npgsql;
using Orleans;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed record GarnetRebuildResult(
    int Discovered,
    int Written,
    int Deleted,
    int Errors,
    long PolicyRevision,
    long PolicyInvalidationVersion,
    bool PolicyRevisionWritten,
    DateTimeOffset CompletedAt);

/// <summary>
/// Rebuilds the Garnet auth projection from the product registry/Orleans and the
/// content-policy revision from PostgreSQL. Garnet is disposable cache state; no
/// business data is inferred from its contents.
/// </summary>
public sealed class GarnetProjectionRebuildService(
    NpgsqlDataSource dataSource,
    IClusterClient cluster,
    GarnetWriteThroughService garnet,
    GarnetPolicyRevisionRebuildService policyRevisionRebuild,
    ILogger<GarnetProjectionRebuildService> logger)
{
    public async Task<GarnetRebuildResult> RebuildAsync(CancellationToken ct = default)
    {
        var entries = new List<RegistryEntry>();
        await using (var command = dataSource.CreateCommand("""
            SELECT entity_key, entity_id, status
            FROM entity_registry
            WHERE entity_type = 'apiKey'
            ORDER BY entity_key
            """))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                entries.Add(new RegistryEntry(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2)));
            }
        }

        var written = 0;
        var deleted = 0;
        var errors = 0;
        var policyRevision = 0L;
        var policyInvalidationVersion = 0L;
        var policyRevisionWritten = false;
        try
        {
            var policyResult = await policyRevisionRebuild.RebuildAsync(ct);
            policyRevision = policyResult.PolicyRevision;
            policyInvalidationVersion = policyResult.InvalidationVersion;
            policyRevisionWritten = true;
        }
        catch (Exception ex)
        {
            errors++;
            logger.LogWarning(ex,
                "Unable to rebuild Garnet content policy revision projection");
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry.EntityId is null ||
                    !string.Equals(entry.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    garnet.EvictAuthSnapshot(entry.EntityKey);
                    deleted++;
                    continue;
                }

                var key = cluster.GetGrain<IApiKeyGrain>(entry.EntityKey);
                var projection = await key.GetProjection();
                var user = await cluster.GetGrain<IUserGrain>(projection.UserId)
                    .GetAuthProjection();
                var group = await cluster.GetGrain<IGroupGrain>(projection.GroupId)
                    .GetAuthProjection();

                if (!string.Equals(projection.Status, "active", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(user.Status, "active", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(group.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    garnet.EvictAuthSnapshot(entry.EntityKey);
                    deleted++;
                    continue;
                }

                var snapshot = JsonSerializer.Serialize(new
                {
                    version = projection.Version,
                    api_key_id = projection.ApiKeyId,
                    user_id = projection.UserId,
                    group_id = projection.GroupId,
                    status = projection.Status,
                    rate_multiplier = (double)group.RateMultiplier,
                    rpm_limit = user.RpmLimit,
                });
                garnet.WriteAuthSnapshot(entry.EntityKey, snapshot);
                written++;
            }
            catch (Exception ex)
            {
                errors++;
                logger.LogWarning(ex, "Unable to rebuild Garnet auth projection for {EntityKey}",
                    entry.EntityKey);
            }
        }

        return new GarnetRebuildResult(
            entries.Count, written, deleted, errors, policyRevision,
            policyInvalidationVersion, policyRevisionWritten, DateTimeOffset.UtcNow);
    }

    private sealed record RegistryEntry(string EntityKey, long? EntityId, string Status);
}
