using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Sub2Api.Data.Migration;
using Xunit;

namespace Sub2Api.Host.Tests;

public sealed class CdcInboxStoreTests
{
    [Fact]
    public async Task FencePromotionIsTransactionalAndRollbackable()
    {
        var connectionString = Environment.GetEnvironmentVariable("CDC_PROMOTION_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new MigrationFenceStore(dataSource);

        var canary = await store.PromoteAsync(
            "sub2api", "platform", "target_canary", "promotion probe", "host-test");
        Assert.Equal(2, canary.Epoch);
        Assert.Equal("platform", canary.WritePrimary);
        Assert.Equal("target_canary", canary.Mode);
        var gate = new MigrationWriteGate(store);
        await Assert.ThrowsAsync<MigrationWriteRejectedException>(
            () => gate.AssertPlatformPrimaryAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PromoteAsync(
            "platform", "platform", "target_primary", "blocked primary probe", "host-test"));

        await using (var checkpoint = dataSource.CreateCommand("""
            INSERT INTO cdc_checkpoints(connector_name, source_lsn, snapshot_completed)
            VALUES ('host-test', '0/READY', true)
            ON CONFLICT (connector_name) DO UPDATE SET
                source_lsn = EXCLUDED.source_lsn, snapshot_completed = true
            """))
        {
            await checkpoint.ExecuteNonQueryAsync();
        }
        var primary = await store.PromoteAsync(
            "platform", "platform", "target_primary", "primary probe", "host-test");
        Assert.Equal(3, primary.Epoch);
        await gate.AssertPlatformPrimaryAsync();

        var readOnly = await store.PromoteAsync(
            "platform", "sub2api", "legacy_read_only", "read-only probe", "host-test");
        Assert.Equal(4, readOnly.Epoch);
        await Assert.ThrowsAsync<MigrationWriteRejectedException>(
            () => gate.AssertPlatformPrimaryAsync());

        var rollback = await store.PromoteAsync(
            "sub2api", "sub2api", "legacy_primary", "rollback probe", "host-test");
        Assert.Equal(5, rollback.Epoch);
        Assert.Equal("sub2api", rollback.WritePrimary);
        Assert.Equal("legacy_primary", rollback.Mode);
        var history = await store.GetHistoryAsync(10);
        Assert.Equal(4, history.Count);
        Assert.Equal(5, history[0].ToEpoch);
        Assert.Equal("rollback probe", history[0].Reason);
    }

    [Fact]
    public async Task RestrictedCredentialStorePersistsCiphertextAndHashOnly()
    {
        var connectionString = Environment.GetEnvironmentVariable("CDC_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new CdcCredentialStore(dataSource);
        var eventId = Guid.NewGuid().ToString();
        var envelope = new CredentialEnvelope(
            eventId, 1, "0/CREDENTIAL-TEST", "credential-test", "account", "901",
            "update", 1, "target-key-v1", "enc:v1:Y2lwaGVydGV4dA==",
            new string('b', 64), DateTimeOffset.UtcNow);

        try
        {
            Assert.True(await store.EnqueueAsync(envelope));
            Assert.False(await store.EnqueueAsync(envelope));
            var collision = envelope with { PayloadHash = new string('c', 64) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueAsync(collision));
            await store.MarkAppliedAsync(eventId);
            await using var command = dataSource.CreateCommand("""
                SELECT octet_length(ciphertext), key_version, payload_hash, applied_at IS NOT NULL
                FROM cdc_credential_payloads WHERE event_id = $1
                """);
            command.Parameters.AddWithValue(eventId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetInt32(0) > 0);
            Assert.Equal("target-key-v1", reader.GetString(1));
            Assert.Equal(new string('b', 64), reader.GetString(2));
            Assert.True(reader.GetBoolean(3));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM cdc_credential_payloads WHERE event_id = $1");
            cleanup.Parameters.AddWithValue(eventId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task TargetWriteGateRejectsLegacyPrimary()
    {
        var connectionString = Environment.GetEnvironmentVariable("CDC_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var gate = new MigrationWriteGate(new MigrationFenceStore(dataSource));
        await Assert.ThrowsAsync<MigrationWriteRejectedException>(
            () => gate.AssertPlatformPrimaryAsync());
    }

    [Fact]
    public async Task InboxIsIdempotentReclaimableDeadLetterAndReplayable()
    {
        var connectionString = Environment.GetEnvironmentVariable("CDC_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return; // Opt-in integration test; unit/contract runs must not require a database.

        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        var store = new CdcInboxStore(dataSource, NullLogger<CdcInboxStore>.Instance);
        var eventId = Guid.NewGuid().ToString();
        using var payloadDocument = JsonDocument.Parse("""{"id":901,"role":"user"}""");
        var payload = payloadDocument.RootElement.Clone();
        var envelope = new ChangeEnvelope(
            eventId, 1, "0/CDC-TEST", "cdc-test", "user", "901", "update", 1,
            DateTimeOffset.UtcNow, ChangeEnvelope.ComputePayloadHash(payload), payload);

        try
        {
            Assert.Equal(CdcEnqueueResult.Inserted, await store.EnqueueAsync(envelope, CancellationToken.None));
            Assert.Equal(CdcEnqueueResult.Duplicate, await store.EnqueueAsync(envelope, CancellationToken.None));
            await using (var accepted = dataSource.CreateCommand(
                "SELECT status FROM cdc_sync_acks WHERE event_id = $1"))
            {
                accepted.Parameters.AddWithValue(eventId);
                Assert.Equal("accepted", (string?)await accepted.ExecuteScalarAsync());
            }

            using var changedDocument = JsonDocument.Parse("""{"id":901,"role":"admin"}""");
            var changedPayload = changedDocument.RootElement.Clone();
            var collision = envelope with
            {
                PayloadHash = ChangeEnvelope.ComputePayloadHash(changedPayload),
                Payload = changedPayload
            };
            Assert.Equal(CdcEnqueueResult.IdentityConflict,
                await store.EnqueueAsync(collision, CancellationToken.None));

            Assert.True(await store.TryClaimAsync(eventId, CancellationToken.None));
            await using (var stale = dataSource.CreateCommand("""
                UPDATE cdc_inbox
                SET next_attempt_at = now() - interval '1 second'
                WHERE event_id = $1
                """))
            {
                stale.Parameters.AddWithValue(eventId);
                await stale.ExecuteNonQueryAsync();
            }
            Assert.True(await store.TryClaimAsync(eventId, CancellationToken.None));

            await using (var attempts = dataSource.CreateCommand("""
                UPDATE cdc_inbox SET attempts = 25, status = 'processing'
                WHERE event_id = $1
                """))
            {
                attempts.Parameters.AddWithValue(eventId);
                await attempts.ExecuteNonQueryAsync();
            }
            Assert.True(await store.MarkFailedAsync(envelope,
                new InvalidOperationException("integration dead-letter"), CancellationToken.None));

            var status = await store.GetStatusAsync(eventId, CancellationToken.None);
            Assert.NotNull(status);
            Assert.Equal("dead_letter", status!.Status);
            await using (var dead = dataSource.CreateCommand(
                "SELECT count(*) FROM cdc_dead_letters WHERE event_id = $1"))
            {
                dead.Parameters.AddWithValue(eventId);
                Assert.Equal(1L, (long)(await dead.ExecuteScalarAsync() ?? 0L));
            }

            Assert.True(await store.ReplayDeadLetterAsync(eventId));
            status = await store.GetStatusAsync(eventId, CancellationToken.None);
            Assert.NotNull(status);
            Assert.Equal("pending", status!.Status);
            Assert.Equal(0, status.Attempts);
        }
        finally
        {
            await using (var ackCleanup = dataSource.CreateCommand(
                "DELETE FROM cdc_sync_acks WHERE event_id = $1"))
            {
                ackCleanup.Parameters.AddWithValue(eventId);
                await ackCleanup.ExecuteNonQueryAsync();
            }
            await using (var deadCleanup = dataSource.CreateCommand(
                "DELETE FROM cdc_dead_letters WHERE event_id = $1"))
            {
                deadCleanup.Parameters.AddWithValue(eventId);
                await deadCleanup.ExecuteNonQueryAsync();
            }
            await using (var inboxCleanup = dataSource.CreateCommand(
                "DELETE FROM cdc_inbox WHERE event_id = $1"))
            {
                inboxCleanup.Parameters.AddWithValue(eventId);
                await inboxCleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task CheckpointIsMonotonicAndRequiresDebeziumLastSnapshotMarker()
    {
        var connectionString = Environment.GetEnvironmentVariable("CDC_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new CdcInboxStore(dataSource, NullLogger<CdcInboxStore>.Instance);
        var connector = $"checkpoint-test-{Guid.NewGuid():N}";
        using var payload = JsonDocument.Parse("""{"id":1}""");
        var first = new ChangeEnvelope(
            Guid.NewGuid().ToString(), 1, "0/20", "snapshot-1", "user", "1", "snapshot", 1,
            DateTimeOffset.UtcNow, ChangeEnvelope.ComputePayloadHash(payload.RootElement), payload.RootElement.Clone())
        {
            Snapshot = "true"
        };
        var last = first with
        {
            EventId = Guid.NewGuid().ToString(),
            SourceLsn = "0/40",
            TransactionId = "snapshot-last",
            Snapshot = "last"
        };
        var late = first with
        {
            EventId = Guid.NewGuid().ToString(),
            SourceLsn = "0/30",
            TransactionId = "late-stream",
            Operation = "update",
            Snapshot = "false"
        };

        try
        {
            await store.UpdateCheckpointAsync(connector, first, false, 0, 10, CancellationToken.None);
            await store.UpdateCheckpointAsync(connector, last, true, 0, 20, CancellationToken.None);
            await store.UpdateCheckpointAsync(connector, late, false, 0, 21, CancellationToken.None);

            await using var command = dataSource.CreateCommand("""
                SELECT source_lsn, snapshot_completed, last_partition, last_offset
                FROM cdc_checkpoints WHERE connector_name = $1
                """);
            command.Parameters.AddWithValue(connector);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("0/40", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(20, reader.GetInt64(3));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM cdc_checkpoints WHERE connector_name = $1");
            cleanup.Parameters.AddWithValue(connector);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
