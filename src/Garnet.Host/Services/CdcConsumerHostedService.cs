using System.Text.Json;
using Confluent.Kafka;
using Orleans;
using Sub2Api.Data.Migration;

namespace Sub2Api.Host.Services;

public sealed class CdcConsumerHostedService(
    IConfiguration configuration,
    CdcInboxStore inbox,
    MigrationFenceStore fence,
    CdcGrainApplier applier,
    ILogger<CdcConsumerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Cdc:Enabled"))
        {
            logger.LogInformation("CDC consumer disabled; set Cdc:Enabled=true only after snapshot/bootstrap validation");
            return;
        }

        var config = new ConsumerConfig
        {
            BootstrapServers = configuration["Cdc:BootstrapServers"] ?? "redpanda:9092",
            GroupId = configuration["Cdc:GroupId"] ?? "platform-cdc-applier",
            ClientId = $"platform-cdc-{Environment.ProcessId}",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = false,
        };
        var topic = configuration["Cdc:Topic"] ?? "sub2api.cdc.v1";
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);
        logger.LogInformation("CDC consumer subscribed to {Topic} at {BootstrapServers}", topic, config.BootstrapServers);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex)
                {
                    logger.LogError(ex, "CDC broker consume failure");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                if (result is null) continue;

                bool committed;
                try
                {
                    committed = await ProcessAsync(result, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "CDC processing infrastructure failure; offset remains uncommitted");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                if (committed)
                {
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                }
            }
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        var json = result.Message.Value ?? "";
        ChangeEnvelope envelope;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("event_id", out _)
                && !document.RootElement.TryGetProperty("op", out _)
                && document.RootElement.TryGetProperty("ts_ms", out _))
            {
                // Debezium heartbeat records have no row operation and are not
                // business events; acknowledge them without polluting rejected
                // message evidence.
                logger.LogDebug("Ignoring Debezium heartbeat at {Topic}[{Partition}]@{Offset}",
                    result.Topic, result.Partition.Value, result.Offset.Value);
                return true;
            }
            envelope = document.RootElement.TryGetProperty("event_id", out _)
                ? JsonSerializer.Deserialize<ChangeEnvelope>(json, CdcJson.Options)
                    ?? throw new FormatException("empty CDC envelope")
                : DebeziumEnvelopeAdapter.Adapt(document.RootElement,
                    configuration.GetValue<long>("Cdc:SourceEpoch", 1));
            envelope.Validate();
            if (!string.Equals(ChangeEnvelope.ComputePayloadHash(envelope.Payload), envelope.PayloadHash, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("payload_hash mismatch");
        }
        catch (Exception ex)
        {
            await inbox.RecordRejectedAsync(
                configuration["Cdc:ConnectorName"] ?? "sub2api-postgres",
                result.Topic, result.Partition.Value, result.Offset.Value, json, ex, ct);
            logger.LogError(ex,
                "Invalid CDC envelope at {Topic}[{Partition}]@{Offset}; digest recorded and offset will be committed",
                result.Topic, result.Partition.Value, result.Offset.Value);
            return true;
        }

        var enqueueResult = await inbox.EnqueueAsync(envelope, ct);
        if (enqueueResult == CdcEnqueueResult.IdentityConflict)
        {
            var error = new InvalidOperationException(
                $"event_id {envelope.EventId} already exists with a different payload_hash");
            await inbox.RecordRejectedAsync(
                configuration["Cdc:ConnectorName"] ?? "sub2api-postgres",
                result.Topic, result.Partition.Value, result.Offset.Value, json, error, ct);
            logger.LogError(error,
                "CDC identity conflict at {Topic}[{Partition}]@{Offset}; digest recorded and offset will be committed",
                result.Topic, result.Partition.Value, result.Offset.Value);
            return true;
        }

        if (enqueueResult == CdcEnqueueResult.Duplicate)
        {
            var status = await inbox.GetStatusAsync(envelope.EventId, ct);
            if (status is null || status.Status is "applied" or "dead_letter") return true;
            if (status.NextAttemptAt > DateTimeOffset.UtcNow)
            {
                await Task.Delay(status.NextAttemptAt - DateTimeOffset.UtcNow, ct);
            }
        }

        if (!await inbox.TryClaimAsync(envelope.EventId, ct))
        {
            var status = await inbox.GetStatusAsync(envelope.EventId, ct);
            return status is null || status.Status is "applied" or "dead_letter";
        }

        try
        {
            var current = await fence.GetAsync(ct);
            if (envelope.Epoch > current.Epoch)
                throw new InvalidOperationException($"event epoch {envelope.Epoch} is ahead of fence epoch {current.Epoch}");
            await applier.ApplyAsync(envelope, ct);
            await inbox.MarkAppliedAsync(envelope, ct);
            await inbox.UpdateCheckpointAsync(configuration["Cdc:ConnectorName"] ?? "sub2api-postgres",
                envelope, string.Equals(envelope.Snapshot, "last", StringComparison.OrdinalIgnoreCase),
                result.Partition.Value, result.Offset.Value, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CDC event {EventId} failed; leaving offset uncommitted for retry", envelope.EventId);
            return await inbox.MarkFailedAsync(envelope, ex, ct);
        }
    }
}
