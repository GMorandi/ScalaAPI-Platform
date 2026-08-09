using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class ChannelMonitorStoreTests
{
    [Fact]
    public async Task MonitorChecksRequireActiveAccountsAndWriteBoundedAuditEvidence()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var actorId = 9_900_000L + Random.Shared.Next(1, 50_000);
        var accountName = $"monitor-test-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        long accountId = 0;
        try
        {
            await using (var account = dataSource.CreateCommand("""
                INSERT INTO accounts(name, platform, type, base_url)
                VALUES ($1, 'test', 'custom', 'https://provider.invalid')
                RETURNING id
                """))
            {
                account.Parameters.AddWithValue(accountName);
                accountId = Convert.ToInt64(await account.ExecuteScalarAsync());
            }

            var store = new ChannelMonitorStore(dataSource);
            var created = await store.RecordAsync(actorId,
                new ChannelCheckRequest(accountId, "healthy", 42, null), "127.0.0.1");
            var invalid = await store.RecordAsync(actorId,
                new ChannelCheckRequest(accountId, "healthy", 700_000, "bad"), null);
            var missing = await store.RecordAsync(actorId,
                new ChannelCheckRequest(accountId + 10_000, "healthy", 1, null), null);

            Assert.Equal(ChannelMonitorWriteStatus.Created, created.Status);
            Assert.Equal(ChannelMonitorWriteStatus.Invalid, invalid.Status);
            Assert.Equal(ChannelMonitorWriteStatus.AccountNotFound, missing.Status);
            var listed = await store.ListAsync(accountId, 1, 50);
            var item = Assert.Single(listed);
            Assert.Equal("healthy", item.Status);
            Assert.Equal(42, item.LatencyMs);

            await using var audit = dataSource.CreateCommand("""
                SELECT count(*) FROM audit_logs
                WHERE user_id = $1 AND action = 'channel_monitor.checked'
                """);
            audit.Parameters.AddWithValue(actorId);
            Assert.Equal(1L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
        }
        finally
        {
            if (accountId > 0)
            {
                await using var monitor = dataSource.CreateCommand(
                    "DELETE FROM channel_monitors WHERE account_id = $1");
                monitor.Parameters.AddWithValue(accountId);
                await monitor.ExecuteNonQueryAsync();
                await using var account = dataSource.CreateCommand(
                    "DELETE FROM accounts WHERE id = $1");
                account.Parameters.AddWithValue(accountId);
                await account.ExecuteNonQueryAsync();
            }
            await using var audit = dataSource.CreateCommand(
                "DELETE FROM audit_logs WHERE user_id = $1 AND action = 'channel_monitor.checked'");
            audit.Parameters.AddWithValue(actorId);
            await audit.ExecuteNonQueryAsync();
        }
    }
}
