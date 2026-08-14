using Npgsql;
using ScalaAPI.Data.Provider;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ProviderCredentialRefreshAuditStoreTests
{
    [Fact]
    public async Task RecordsSecretFreeOutcomeHistoryAndFiltersIt()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var accountId = 9_000_000L + Random.Shared.Next(1, 900_000);
        var successId = Guid.NewGuid();
        var failureId = Guid.NewGuid();
        var revokedId = Guid.NewGuid();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            var store = new ProviderCredentialRefreshAuditStore(dataSource);
            var started = DateTime.UtcNow.AddMilliseconds(-20);
            await store.RecordAsync(successId, accountId, "dispatch", 1, 2,
                "succeeded", null, "provider-mock", started, DateTime.UtcNow, 20);
            await store.RecordAsync(failureId, accountId, "media", 2, null,
                "failed", "oauth_token_endpoint_status_400", "provider-mock",
                started, DateTime.UtcNow, 25);
            await store.RecordAsync(revokedId, accountId, "dispatch", 2, 3,
                "revoked", "oauth_refresh_token_revoked", "provider-mock",
                started, DateTime.UtcNow, 30);

            Assert.Equal(3, await store.CountAsync(accountId));
            Assert.Equal(1, await store.CountAsync(accountId, "succeeded", "dispatch"));
            var failed = await store.ListAsync(accountId, 1, 10, "failed", "media");
            var entry = Assert.Single(failed);
            Assert.Equal(failureId, entry.AttemptId);
            Assert.Equal("oauth_token_endpoint_status_400", entry.ErrorCode);
            Assert.Equal("provider-mock", entry.TokenEndpointHost);
            Assert.DoesNotContain("mock-secret", entry.ErrorCode ?? "");
            var revoked = Assert.Single(await store.ListAsync(
                accountId, 1, 10, "revoked", "dispatch"));
            Assert.Equal(revokedId, revoked.AttemptId);
            Assert.Equal(3, revoked.VersionAfter);
            Assert.Equal("oauth_refresh_token_revoked", revoked.ErrorCode);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM provider_credential_refresh_attempts WHERE account_id = $1");
            cleanup.Parameters.AddWithValue(accountId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
