using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class MediaOperationStoreTests
{
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
}
