using Npgsql;

namespace ScalaAPI.Host.Services;

public sealed record ContentPolicyPropagationResult(int Claimed, int Propagated, int Failed);

internal static class ContentPolicyPropagationLock
{
    public const long Key = 785349201;
}

/// <summary>
/// Drains the PostgreSQL content-policy change outbox. Garnet only carries the
/// disposable revision and invalidation signal; a failed propagation remains
/// retryable and never makes PostgreSQL policy state disappear.
/// </summary>
public sealed class ContentPolicyPropagationService(
    NpgsqlDataSource dataSource,
    GarnetWriteThroughService garnet,
    ILogger<ContentPolicyPropagationService> logger)
{
    public async Task<ContentPolicyPropagationResult> PropagateOnceAsync(
        string workerId, CancellationToken ct = default)
    {
        var events = await ClaimAsync(workerId, ct);
        var propagated = 0;
        var failed = 0;
        foreach (var change in events)
        {
            try
            {
                await PublishOneAsync(change, workerId, ct);
                propagated++;
            }
            catch (Exception ex)
            {
                failed++;
                var message = ex.Message.Length > 1000
                    ? ex.Message[..1000] : ex.Message;
                await MarkFailedAsync(change.Id, workerId, message, ct);
                logger.LogWarning(ex,
                    "Content policy revision {Revision} propagation failed for event {EventId}",
                    change.Revision, change.Id);
            }
        }

        return new ContentPolicyPropagationResult(events.Count, propagated, failed);
    }

    private async Task PublishOneAsync(PendingChange change, string workerId,
        CancellationToken ct)
    {
        // Claims are independent across processes. Only the short publication
        // section is serialized, so a slow Garnet call does not prevent another
        // worker from claiming unrelated outbox rows. The monotonic Garnet
        // operation makes a later revision win when workers finish out of order.
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand(
            $"SELECT pg_advisory_xact_lock({ContentPolicyPropagationLock.Key})",
            connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        garnet.PublishContentPolicyRevision(change.Revision);
        await MarkPropagatedAsync(change.Id, workerId, ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<IReadOnlyList<PendingChange>> ClaimAsync(
        string workerId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT id
                FROM content_policy_change_events
                WHERE propagated_at IS NULL
                  AND (claimed_until IS NULL OR claimed_until < now())
                ORDER BY id
                LIMIT 50
                FOR UPDATE SKIP LOCKED
            )
            UPDATE content_policy_change_events AS events
            SET claimed_by = $1,
                claimed_until = now() + interval '30 seconds',
                attempts = events.attempts + 1
            FROM candidates
            WHERE events.id = candidates.id
            RETURNING events.id, events.revision
            """, connection, transaction);
        command.Parameters.AddWithValue(workerId);
        var result = new List<PendingChange>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                result.Add(new PendingChange(reader.GetInt64(0), reader.GetInt64(1)));
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task MarkPropagatedAsync(long eventId, string workerId,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE content_policy_change_events
            SET propagated_at = now(), claimed_by = NULL, claimed_until = NULL,
                last_error = NULL
            WHERE id = $1 AND claimed_by = $2
            """);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(workerId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkFailedAsync(long eventId, string workerId, string error,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE content_policy_change_events
            SET claimed_by = NULL, claimed_until = NULL, last_error = $3
            WHERE id = $1 AND claimed_by = $2
            """);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(error);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record PendingChange(long Id, long Revision);
}

public sealed class ContentPolicyPropagationHostedService(
    ContentPolicyPropagationService propagation,
    ILogger<ContentPolicyPropagationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"content-policy-{Environment.ProcessId}-{Guid.NewGuid():N}";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await propagation.PropagateOnceAsync(workerId, stoppingToken);
                if (result.Claimed == 0)
                    await Task.Delay(250, stoppingToken);
                else if (result.Failed > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Content policy propagation iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
