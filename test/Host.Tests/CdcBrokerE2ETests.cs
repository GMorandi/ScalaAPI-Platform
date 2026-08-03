using Confluent.Kafka;
using Npgsql;
using System.Text.Json;
using Sub2Api.Data.Migration;
using Xunit;

namespace Sub2Api.Host.Tests;

public sealed class CdcBrokerE2ETests
{
    [Fact]
    public async Task DebeziumSnapshotThenPostSnapshotLsnIsContinuous()
    {
        var bootstrap = Environment.GetEnvironmentVariable("CDC_BROKER_BOOTSTRAP");
        var sourceConnection = Environment.GetEnvironmentVariable("CDC_SOURCE_CONNECTION");
        if (string.IsNullOrWhiteSpace(bootstrap) || string.IsNullOrWhiteSpace(sourceConnection)) return;

        var topic = Environment.GetEnvironmentVariable("CDC_BROKER_TOPIC") ?? "sub2api.cdc.v1";
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = $"host-e2e-{Guid.NewGuid():N}",
            ClientId = $"host-e2e-{Environment.ProcessId}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var dataEvents = 0;
        var sawSnapshotLast = false;
        decimal snapshotLsn = 0;
        var sawSemanticOutbox = false;

        while (DateTime.UtcNow < deadline && !sawSnapshotLast)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message?.Value is null) continue;
            using var document = JsonDocument.Parse(result.Message.Value);
            if (!document.RootElement.TryGetProperty("op", out _)) continue; // Debezium heartbeat.

            var envelope = DebeziumEnvelopeAdapter.Adapt(document.RootElement, 1);
            envelope.Validate();
            Assert.True(ChangeEnvelope.TryParseLsn(envelope.SourceLsn, out var lsn));
            Assert.True(lsn >= snapshotLsn);
            dataEvents++;
            if (envelope.AggregateType == "api_key")
            {
                sawSemanticOutbox = true;
                Assert.Equal("snapshot", envelope.Operation);
                Assert.DoesNotContain("key", envelope.Payload.EnumerateObject().Select(x => x.Name));
            }
            if (envelope.Snapshot == "last")
            {
                sawSnapshotLast = true;
                snapshotLsn = lsn;
            }
        }

        Assert.True(sawSnapshotLast, "Debezium did not emit the final snapshot marker");
        Assert.True(dataEvents >= 9, $"expected all probe tables, got {dataEvents}");
        Assert.True(sawSemanticOutbox, "semantic API-key outbox was not snapshotted");

        await using (var source = new NpgsqlConnection(sourceConnection))
        {
            await source.OpenAsync();
            await using var update = new NpgsqlCommand(
                "UPDATE users SET status = 'disabled' WHERE id = 1", source);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        decimal? postSnapshotLsn = null;
        while (DateTime.UtcNow < deadline && postSnapshotLsn is null)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message?.Value is null) continue;
            using var document = JsonDocument.Parse(result.Message.Value);
            if (!document.RootElement.TryGetProperty("op", out _)) continue;
            var envelope = DebeziumEnvelopeAdapter.Adapt(document.RootElement, 1);
            envelope.Validate();
            if (envelope.AggregateType == "user" && envelope.AggregateId == "1"
                && envelope.Operation == "update")
            {
                Assert.True(ChangeEnvelope.TryParseLsn(envelope.SourceLsn, out var lsn));
                postSnapshotLsn = lsn;
            }
        }

        Assert.True(postSnapshotLsn.HasValue, "post-snapshot update was not consumed");
        Assert.True(postSnapshotLsn.Value > snapshotLsn,
            $"post-snapshot LSN {postSnapshotLsn} did not advance past snapshot {snapshotLsn}");
        consumer.Close();
    }
}
