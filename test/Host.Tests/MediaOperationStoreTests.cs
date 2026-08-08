using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class MediaOperationStoreTests
{
    [Fact]
    public async Task RequestLeasePersistsAndFinalizesDurableBalanceHoldIdempotently()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-hold-{suffix}";
        var duplicateLeaseToken = $"lease-hold-duplicate-{suffix}";
        var requestId = $"request-hold-{suffix}";
        var holdId = $"hold-{suffix}";
        var abortLeaseToken = $"lease-abort-hold-{suffix}";
        var abortRequestId = $"request-abort-hold-{suffix}";
        var abortHoldId = $"hold-abort-{suffix}";
        var retryLeaseToken = $"lease-retry-hold-{suffix}";
        var retryRequestId = $"request-retry-hold-{suffix}";
        var retryHoldId = $"hold-retry-{suffix}";
        var retryLeaseToken2 = $"lease-retry-hold-2-{suffix}";
        var retryRequestId2 = $"request-retry-hold-2-{suffix}";
        var retryHoldId2 = $"hold-retry-2-{suffix}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "1"
            }).Build();
        var store = new RequestLeaseStore(dataSource,
            new ModelPricingService(configuration), NullLogger<RequestLeaseStore>.Instance);
        try
        {
            var request = new LeaseCreateRequest(
                leaseToken, requestId, "hash-hold", 94001, 95001, 96001, 97001,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, holdId, 10m,
                DateTime.UtcNow.AddMinutes(10), "idem-hold-" + suffix, "fingerprint-a");
            Assert.True(await store.CreateAsync(request));

            await using (var active = dataSource.CreateCommand(
                "SELECT status, lease_token, amount FROM balance_holds WHERE hold_id = $1"))
            {
                active.Parameters.AddWithValue(holdId);
                await using var reader = await active.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("active", reader.GetString(0));
                Assert.Equal(leaseToken, reader.GetString(1));
                Assert.Equal(10m, reader.GetDecimal(2));
            }

            var duplicate = request with { LeaseToken = duplicateLeaseToken };
            Assert.False(await store.CreateAsync(duplicate));

            var replay = await store.CreateDetailedAsync(request with
            {
                LeaseToken = duplicateLeaseToken,
                RequestId = requestId + "-replay",
                HoldHandle = holdId + "-replay"
            });
            Assert.True(replay.Replay);

            var conflict = await store.CreateDetailedAsync(request with
            {
                LeaseToken = duplicateLeaseToken,
                RequestId = requestId + "-conflict",
                HoldHandle = holdId + "-conflict",
                RequestFingerprint = "fingerprint-b"
            });
            Assert.True(conflict.Conflict);

            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 100, 0, 0, 0, 20, 0, 200, false, false,
                ResponseStatusCode: 200, ResponseContentType: "application/json",
                ResponseBody: "{\"ok\":true}"));
            Assert.True(completed.Accepted);
            Assert.False(completed.Duplicate);
            Assert.Equal(10m, await ReadHoldAmount(dataSource, holdId));
            Assert.Equal("committed", await ReadHoldStatus(dataSource, holdId));
            Assert.Equal("completed", await ReadIdempotencyStatus(dataSource,
                94001, "idem-hold-" + suffix));
            var replayLookup = await store.CheckIdempotencyAsync(
                94001, "idem-hold-" + suffix, "fingerprint-a");
            Assert.True(replayLookup.Found);
            Assert.False(replayLookup.Conflict);
            Assert.True(replayLookup.HasResponse);
            Assert.Equal(200, replayLookup.ResponseStatusCode);
            Assert.Equal("application/json", replayLookup.ResponseContentType);
            Assert.Equal("{\"ok\":true}", replayLookup.ResponseBody);
            Assert.True((await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 100, 0, 0, 0, 20, 0, 200, false, false))).Duplicate);

            var abortRequest = new LeaseCreateRequest(
                abortLeaseToken, abortRequestId, "hash-hold", 94001, 95001, 96001, 97001,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, abortHoldId, 10m,
                DateTime.UtcNow.AddMinutes(10));
            Assert.True(await store.CreateAsync(abortRequest));
            Assert.True((await store.AbortAsync(abortLeaseToken, "client_disconnect")).Accepted);
            Assert.Equal("released", await ReadHoldStatus(dataSource, abortHoldId));
            Assert.True((await store.AbortAsync(abortLeaseToken, "client_disconnect")).Duplicate);

            var retryRequest = new LeaseCreateRequest(
                retryLeaseToken, retryRequestId, "hash-hold", 94001, 95001, 96001, 97001,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, retryHoldId, 10m,
                DateTime.UtcNow.AddMinutes(10), "idem-retry-" + suffix, "fingerprint-retry");
            Assert.True(await store.CreateAsync(retryRequest));
            Assert.True((await store.AbortAsync(retryLeaseToken, "upstream_failure")).Accepted);

            var reopened = await store.CreateDetailedAsync(retryRequest with
            {
                LeaseToken = retryLeaseToken2,
                RequestId = retryRequestId2,
                HoldHandle = retryHoldId2,
            });
            Assert.True(reopened.Created);
            Assert.Equal("active", await ReadHoldStatus(dataSource, retryHoldId2));
            Assert.Equal("lease-retry-hold-2-" + suffix,
                await ReadIdempotencyLease(dataSource, 94001, "idem-retry-" + suffix));
        }
        finally
        {
            foreach (var table in new[] { "usage_outbox", "usage_logs", "usage_events" })
            {
                await using var cleanupUsage = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE lease_token IN ($1, $2)");
                cleanupUsage.Parameters.AddWithValue(leaseToken);
                cleanupUsage.Parameters.AddWithValue(abortLeaseToken);
                await cleanupUsage.ExecuteNonQueryAsync();
            }
            await using (var cleanupHolds = dataSource.CreateCommand(
                "DELETE FROM balance_holds WHERE hold_id IN ($1, $2, $3, $4, $5, $6)"))
            {
                cleanupHolds.Parameters.AddWithValue(holdId);
                cleanupHolds.Parameters.AddWithValue(abortHoldId);
                cleanupHolds.Parameters.AddWithValue(holdId + "-replay");
                cleanupHolds.Parameters.AddWithValue(holdId + "-conflict");
                cleanupHolds.Parameters.AddWithValue(retryHoldId);
                cleanupHolds.Parameters.AddWithValue(retryHoldId2);
                await cleanupHolds.ExecuteNonQueryAsync();
            }
            await using var cleanupLeases = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token IN ($1, $2, $3, $4, $5)");
            cleanupLeases.Parameters.AddWithValue(leaseToken);
            cleanupLeases.Parameters.AddWithValue(duplicateLeaseToken);
            cleanupLeases.Parameters.AddWithValue(abortLeaseToken);
            cleanupLeases.Parameters.AddWithValue(retryLeaseToken);
            cleanupLeases.Parameters.AddWithValue(retryLeaseToken2);
            await cleanupLeases.ExecuteNonQueryAsync();

            await using var cleanupIdempotency = dataSource.CreateCommand(
                "DELETE FROM request_idempotency WHERE idempotency_key IN ($1, $2)");
            cleanupIdempotency.Parameters.AddWithValue("idem-hold-" + suffix);
            cleanupIdempotency.Parameters.AddWithValue("idem-retry-" + suffix);
            await cleanupIdempotency.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task DurableLifecycleIsIdempotentClaimableAndTerminal()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-media-{suffix}";
        var requestId = $"request-media-{suffix}";
        const long apiKeyId = 91001;
        try
        {
            await InsertLease(dataSource, leaseToken, requestId);
            var store = new MediaOperationStore(dataSource);

            var created = await store.CreateOrGetAsync(apiKeyId, 92001, requestId,
                leaseToken, "images_generations_async", "idem-" + suffix,
                "fingerprint-a", "openai", DateTime.UtcNow.AddHours(1));
            Assert.True(created.Created);
            Assert.False(created.Conflict);

            var replay = await store.CreateOrGetAsync(apiKeyId, 92001,
                requestId + "-replay", leaseToken, "images_generations_async",
                "idem-" + suffix, "fingerprint-a", "openai",
                DateTime.UtcNow.AddHours(1));
            Assert.False(replay.Created);
            Assert.False(replay.Conflict);
            Assert.Equal(created.Operation.OperationId, replay.Operation.OperationId);

            var conflict = await store.CreateOrGetAsync(apiKeyId, 92001,
                requestId + "-conflict", leaseToken, "images_generations_async",
                "idem-" + suffix, "fingerprint-b", "openai",
                DateTime.UtcNow.AddHours(1));
            Assert.True(conflict.Conflict);

            var running = await store.UpdateAsync(apiKeyId, created.Operation.OperationId,
                "running", 15, upstreamTaskId: "provider-task-1",
                outputMetadata: "{\"status\":\"running\"}");
            Assert.NotNull(running);
            Assert.Equal("provider-task-1", running!.UpstreamTaskId);

            await using (var makeDue = dataSource.CreateCommand("""
                UPDATE media_operations SET next_poll_at = now() - interval '1 second'
                WHERE operation_id = $1
                """))
            {
                makeDue.Parameters.AddWithValue(created.Operation.OperationId);
                await makeDue.ExecuteNonQueryAsync();
            }
            var claimed = await store.ClaimDueAsync(10);
            var claimedOperation = Assert.Single(claimed,
                operation => operation.OperationId == created.Operation.OperationId);
            Assert.Equal(1, claimedOperation.Attempts);
            Assert.NotNull(claimedOperation.NextPollAt);

            var succeeded = await store.UpdateAsync(apiKeyId, created.Operation.OperationId,
                "succeeded", 100, outputMetadata: "{\"status\":\"completed\"}",
                outputUrl: "https://objects.example/output.png", contentType: "image/png");
            Assert.Equal("succeeded", succeeded!.Status);
            Assert.Equal("https://objects.example/output.png", succeeded.OutputUrl);

            var unchanged = await store.UpdateAsync(apiKeyId, created.Operation.OperationId,
                "failed", 100, error: "{\"type\":\"late_failure\"}");
            Assert.Equal("succeeded", unchanged!.Status);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Pricing:Models:gpt-4o:ImageOutputPerUnit"] = "0.08"
                }).Build();
            var leases = new RequestLeaseStore(dataSource,
                new ModelPricingService(configuration),
                NullLogger<RequestLeaseStore>.Instance);
            var settlement = await leases.CompleteAsync(new LeaseCompletion(
                leaseToken, 0, 0, 0, 0, 20, 0, 200, false, false,
                OutputImageCount: 2, ImageSize: "1024x1024",
                MediaOperationId: created.Operation.OperationId,
                PricingVersion: "v1"));
            Assert.True(settlement.Accepted);

            await using var billed = dataSource.CreateCommand("""
                SELECT cost_usd, output_image_count, media_operation_id, pricing_version
                FROM usage_events WHERE lease_token = $1
                """);
            billed.Parameters.AddWithValue(leaseToken);
            await using var billedReader = await billed.ExecuteReaderAsync();
            Assert.True(await billedReader.ReadAsync());
            Assert.Equal(0.16m, billedReader.GetDecimal(0));
            Assert.Equal(2, billedReader.GetInt32(1));
            Assert.Equal(created.Operation.OperationId, billedReader.GetString(2));
            Assert.Equal("v1", billedReader.GetString(3));
        }
        finally
        {
            await using (var cleanupMedia = dataSource.CreateCommand(
                "DELETE FROM media_operations WHERE api_key_id = $1"))
            {
                cleanupMedia.Parameters.AddWithValue(apiKeyId);
                await cleanupMedia.ExecuteNonQueryAsync();
            }
            foreach (var table in new[] { "usage_outbox", "usage_logs", "usage_events" })
            {
                await using var cleanupUsage = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE lease_token = $1");
                cleanupUsage.Parameters.AddWithValue(leaseToken);
                await cleanupUsage.ExecuteNonQueryAsync();
            }
            await using (var cleanupLease = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token = $1"))
            {
                cleanupLease.Parameters.AddWithValue(leaseToken);
                await cleanupLease.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task ExpiryReleasesHoldAndAllowsMatchingRetry()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-expiry-{suffix}";
        var requestId = $"request-expiry-{suffix}";
        var retryToken = $"lease-expiry-retry-{suffix}";
        var retryRequestId = $"request-expiry-retry-{suffix}";
        var holdId = $"hold-expiry-{suffix}";
        var retryHoldId = $"hold-expiry-retry-{suffix}";
        var idempotencyKey = $"idem-expiry-{suffix}";
        const long apiKeyId = 98001;
        try
        {
            var store = new RequestLeaseStore(dataSource,
                new ModelPricingService(new ConfigurationBuilder().Build()),
                NullLogger<RequestLeaseStore>.Instance);
            var request = new LeaseCreateRequest(
                leaseToken, requestId, "hash-expiry", apiKeyId, 98002, 98003, 98004,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, holdId, 3m,
                DateTime.UtcNow.AddMinutes(-1), idempotencyKey, "expiry-fingerprint");
            Assert.True(await store.CreateAsync(request));
            Assert.Equal(1, await store.ExpireActiveAsync());
            Assert.Equal("expired", await ReadLeaseStatus(dataSource, leaseToken));
            Assert.Equal("released", await ReadHoldStatus(dataSource, holdId));
            Assert.Equal("expired", await ReadIdempotencyStatus(dataSource, apiKeyId, idempotencyKey));

            var retry = await store.CreateDetailedAsync(request with
            {
                LeaseToken = retryToken,
                RequestId = retryRequestId,
                HoldHandle = retryHoldId,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            });
            Assert.True(retry.Created);
            Assert.Equal("active", await ReadHoldStatus(dataSource, retryHoldId));
        }
        finally
        {
            foreach (var table in new[] { "usage_outbox", "usage_logs", "usage_events" })
            {
                await using var cleanupUsage = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE lease_token IN ($1, $2)");
                cleanupUsage.Parameters.AddWithValue(leaseToken);
                cleanupUsage.Parameters.AddWithValue(retryToken);
                await cleanupUsage.ExecuteNonQueryAsync();
            }
            await using (var cleanupHolds = dataSource.CreateCommand(
                "DELETE FROM balance_holds WHERE hold_id IN ($1, $2)"))
            {
                cleanupHolds.Parameters.AddWithValue(holdId);
                cleanupHolds.Parameters.AddWithValue(retryHoldId);
                await cleanupHolds.ExecuteNonQueryAsync();
            }
            await using (var cleanupIdempotency = dataSource.CreateCommand(
                "DELETE FROM request_idempotency WHERE api_key_id = $1 AND idempotency_key = $2"))
            {
                cleanupIdempotency.Parameters.AddWithValue(apiKeyId);
                cleanupIdempotency.Parameters.AddWithValue(idempotencyKey);
                await cleanupIdempotency.ExecuteNonQueryAsync();
            }
            await using var cleanupLeases = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token IN ($1, $2)");
            cleanupLeases.Parameters.AddWithValue(leaseToken);
            cleanupLeases.Parameters.AddWithValue(retryToken);
            await cleanupLeases.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ExpiredSettlementOutboxSurvivesRestartClaimsAndRetryExhaustion()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-outbox-recovery-{suffix}";
        var requestId = $"request-outbox-recovery-{suffix}";
        var holdId = $"hold-outbox-recovery-{suffix}";
        var store = new RequestLeaseStore(dataSource,
            new ModelPricingService(new ConfigurationBuilder().Build()),
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, requestId, "hash-outbox-recovery", 98101, 98102, 98103, 98104,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, holdId, 3m,
                DateTime.UtcNow.AddMinutes(-1))));
            Assert.Equal(1, await store.ExpireActiveAsync());

            // Simulate a process dying after claiming the durable event.
            var firstClaim = Assert.Single(await store.ClaimOutboxBatchAsync("test-recovery-1"));
            Assert.Equal("expire", firstClaim.Item.EventType);
            await using (var expireClaim = dataSource.CreateCommand("""
                UPDATE usage_outbox
                SET claimed_until = now() - interval '1 second'
                WHERE id = $1
                """))
            {
                expireClaim.Parameters.AddWithValue(firstClaim.Item.Id);
                await expireClaim.ExecuteNonQueryAsync();
            }

            var secondClaim = Assert.Single(await store.ClaimOutboxBatchAsync("test-recovery-2"));
            Assert.Equal(firstClaim.Item.Id, secondClaim.Item.Id);

            // Settlement errors must remain claimable even after the old
            // 25-attempt threshold that previously dead-lettered financial work.
            var error = new InvalidOperationException("transient grain unavailable");
            for (var i = 0; i < 26; i++)
                await store.MarkRetryAsync(secondClaim.Item with { Attempts = i }, error);

            // Also cover recovery of a row written by the previous process
            // version, which could have set dead_lettered_at at this point.
            await using (var quarantine = dataSource.CreateCommand(
                "UPDATE usage_outbox SET dead_lettered_at = now() WHERE id = $1"))
            {
                quarantine.Parameters.AddWithValue(firstClaim.Item.Id);
                await quarantine.ExecuteNonQueryAsync();
            }
            Assert.True(await store.RequeueUnprocessedDeadLettersAsync() >= 1);

            await using (var makeDue = dataSource.CreateCommand("""
                UPDATE usage_outbox
                SET next_attempt_at = now(), claimed_until = NULL, claimed_by = NULL
                WHERE id = $1
                """))
            {
                makeDue.Parameters.AddWithValue(firstClaim.Item.Id);
                await makeDue.ExecuteNonQueryAsync();
            }

            var recovered = Assert.Single(await store.ClaimOutboxBatchAsync("test-recovery-3"));
            Assert.Equal(firstClaim.Item.Id, recovered.Item.Id);
            await using var state = dataSource.CreateCommand(
                "SELECT processed_at, dead_lettered_at, attempts FROM usage_outbox WHERE id = $1");
            state.Parameters.AddWithValue(firstClaim.Item.Id);
            await using var reader = await state.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(26, reader.GetInt32(2));
        }
        finally
        {
            await using (var cleanupOutbox = dataSource.CreateCommand(
                "DELETE FROM usage_outbox WHERE lease_token = $1"))
            {
                cleanupOutbox.Parameters.AddWithValue(leaseToken);
                await cleanupOutbox.ExecuteNonQueryAsync();
            }
            await using (var cleanupHold = dataSource.CreateCommand(
                "DELETE FROM balance_holds WHERE hold_id = $1"))
            {
                cleanupHold.Parameters.AddWithValue(holdId);
                await cleanupHold.ExecuteNonQueryAsync();
            }
            await using var cleanupLease = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token = $1");
            cleanupLease.Parameters.AddWithValue(leaseToken);
            await cleanupLease.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertLease(NpgsqlDataSource dataSource,
        string leaseToken, string requestId)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO request_leases (
                lease_token, request_id, api_key_hash, api_key_id, user_id,
                account_id, group_id, model, upstream_model, inbound_endpoint,
                rate_multiplier, hold_handle, hold_amount, status, expires_at)
            VALUES ($1, $2, 'hash', 91001, 93001, 92001, 94001,
                'gpt-4o', 'gpt-4o', 'images', 1, NULL, 10, 'active', now() + interval '1 hour')
            """);
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(requestId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadHoldStatus(NpgsqlDataSource dataSource, string holdId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT status FROM balance_holds WHERE hold_id = $1");
        command.Parameters.AddWithValue(holdId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadLeaseStatus(NpgsqlDataSource dataSource, string leaseToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT status FROM request_leases WHERE lease_token = $1");
        command.Parameters.AddWithValue(leaseToken);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<decimal> ReadHoldAmount(NpgsqlDataSource dataSource, string holdId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT amount FROM balance_holds WHERE hold_id = $1");
        command.Parameters.AddWithValue(holdId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadIdempotencyStatus(NpgsqlDataSource dataSource,
        long apiKeyId, string idempotencyKey)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT status FROM request_idempotency WHERE api_key_id = $1 AND idempotency_key = $2");
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(idempotencyKey);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadIdempotencyLease(NpgsqlDataSource dataSource,
        long apiKeyId, string idempotencyKey)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT lease_token FROM request_idempotency WHERE api_key_id = $1 AND idempotency_key = $2");
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(idempotencyKey);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
