using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class ApiKeyAuditStoreTests
{
    [Fact]
    public async Task ListIsFilteredPagedAndDoesNotExposeKeyMaterial()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var apiKeyId = 8_500_000L + Random.Shared.Next(1, 900_000);
        var userId = apiKeyId + 1_000_000L;
        var actorId = userId + 1_000_000L;
        var hash = $"audit-key-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var insertKey = dataSource.CreateCommand("""
                INSERT INTO user_api_keys
                    (api_key_id, user_email, key_hash, key_prefix, status, created_at, scopes)
                VALUES ($1, $2, $3, $4, 'active', now(), '["messages"]'::jsonb)
                """))
            {
                insertKey.Parameters.AddWithValue(apiKeyId);
                insertKey.Parameters.AddWithValue($"audit-{apiKeyId}@example.test");
                insertKey.Parameters.AddWithValue(hash);
                insertKey.Parameters.AddWithValue("sk-audit");
                await insertKey.ExecuteNonQueryAsync();
            }

            var store = new ApiKeyAuditStore(dataSource);
            await store.RecordAsync(apiKeyId, userId, actorId, "created",
                ["messages"], null, requestId: "audit-request");
            await store.RecordAsync(apiKeyId, userId, actorId, "denied",
                ["messages"], null, capability: "images_sync", reason: "scope denied");
            await store.RecordAsync(apiKeyId, userId, actorId, "revoked",
                ["messages"], null, reason: "operator revoke");

            var page = await store.ListAsync(apiKeyId, "denied", null, null, 1, 1);
            Assert.Equal(1, page.Total);
            var entry = Assert.Single(page.Items);
            Assert.Equal("denied", entry.Action);
            Assert.Equal("images_sync", entry.Capability);
            Assert.Equal(actorId, entry.ActorUserId);
            Assert.Null(entry.RequestId);

            var secondPage = await store.ListAsync(apiKeyId, null, null, null, 2, 2);
            Assert.Equal(3, secondPage.Total);
            Assert.Single(secondPage.Items);
            Assert.Equal("created", secondPage.Items[0].Action);
        }
        finally
        {
            await using (var auditCleanup = dataSource.CreateCommand(
                "DELETE FROM api_key_audit_events WHERE api_key_id = $1"))
            {
                auditCleanup.Parameters.AddWithValue(apiKeyId);
                await auditCleanup.ExecuteNonQueryAsync();
            }
            await using (var keyCleanup = dataSource.CreateCommand(
                "DELETE FROM user_api_keys WHERE api_key_id = $1"))
            {
                keyCleanup.Parameters.AddWithValue(apiKeyId);
                await keyCleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
