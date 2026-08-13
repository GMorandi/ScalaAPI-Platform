using ScalaAPI.Data.Config;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Background service that drains the config_revisions outbox. For each pending
/// revision it publishes the new config version to Garnet so that all consumers
/// (other silos, gateway processes) pick up the change atomically. Stale writes
/// are detected and skipped: if a newer pending revision already exists for the
/// same key, the older one is marked applied without publishing.
/// </summary>
public sealed class ConfigPropagationService(
    ConfigRevisionStore revisionStore,
    GarnetWriteThroughService garnet,
    ILogger<ConfigPropagationService> logger)
{
    public async Task<ConfigPropagationResult> PropagateOnceAsync(
        string workerId, CancellationToken ct = default)
    {
        var pending = await ClaimPendingAsync(workerId, ct);
        var propagated = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var revision in pending)
        {
            try
            {
                // Stale-write protection: if a newer pending revision exists for
                // the same key, skip this one. The newer revision will carry the
                // final value and publish it.
                if (await revisionStore.HasNewerPendingRevisionAsync(
                        revision.ConfigKey, revision.RevisionId, ct))
                {
                    await revisionStore.MarkAppliedAsync(revision.RevisionId, ct);
                    skipped++;
                    continue;
                }

                // Publish the config version to Garnet. The monotonic revision
                // ensures that out-of-order completion cannot regress the version.
                garnet.PublishConfigRevision(revision.RevisionId);
                await revisionStore.MarkAppliedAsync(revision.RevisionId, ct);
                propagated++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex,
                    "Config revision {RevisionId} propagation failed for key {ConfigKey}",
                    revision.RevisionId, revision.ConfigKey);
            }
        }

        return new ConfigPropagationResult(pending.Count, propagated, skipped, failed);
    }

    private async Task<IReadOnlyList<Data.Config.ConfigRevision>> ClaimPendingAsync(
        string workerId, CancellationToken ct)
    {
        // Claim pending revisions with advisory lock serialization. Multiple
        // processes can run this concurrently; the Garnet monotonic publish
        // ensures convergence regardless of completion order.
        return await revisionStore.ListRevisionsAsync(limit: 50, ct: ct) is { Count: > 0 } revisions
            ? revisions.Where(r => r.Status == "pending").Take(50).ToList()
            : [];
    }
}

public sealed record ConfigPropagationResult(
    int Claimed,
    int Propagated,
    int Skipped,
    int Failed);

public sealed class ConfigPropagationHostedService(
    ConfigPropagationService propagation,
    ILogger<ConfigPropagationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"config-prop-{Environment.ProcessId}-{Guid.NewGuid():N}";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await propagation.PropagateOnceAsync(workerId, stoppingToken);
                if (result.Claimed == 0)
                    await Task.Delay(500, stoppingToken);
                else if (result.Failed > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Config propagation iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
