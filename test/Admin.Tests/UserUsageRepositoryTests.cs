using Npgsql;
using ScalaAPI.Data.Entities;
using ScalaAPI.Data.Repositories;
using SqlSugar;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class UserUsageRepositoryTests
{
    [Fact]
    public async Task UsageQueriesRemainScopedToTheAuthenticatedUser()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var userId = 7_100_000L + Random.Shared.Next(1, 800_000);
        var otherUserId = userId + 1;
        var prefix = $"user-web-usage-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var insert = dataSource.CreateCommand("""
            INSERT INTO usage_logs(
                request_id, lease_token, api_key_id, user_id, account_id, group_id,
                model, upstream_model, input_tokens, output_tokens,
                cache_create_tokens, cache_read_tokens, cost_usd, duration_ms,
                first_token_ms, stream, client_disconnect)
            VALUES ($1, $2, 1, $3, 1, 1, 'gpt-4o', 'gpt-4o', 3, 2,
                    0, 0, 0.00001000, 20, 5, false, false),
                   ($4, $5, 2, $6, 1, 1, 'gpt-4o', 'gpt-4o', 9, 4,
                    0, 0, 0.00002000, 30, 7, false, false)
            """))
        {
            insert.Parameters.AddWithValue($"{prefix}-one");
            insert.Parameters.AddWithValue($"{prefix}-lease-one");
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue($"{prefix}-two");
            insert.Parameters.AddWithValue($"{prefix}-lease-two");
            insert.Parameters.AddWithValue(otherUserId);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = DbType.PostgreSQL,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
            var repository = new UsageLogRepository(db);
            var items = await repository.GetPaged(userId, null, null, null, 1, 50);
            var total = await repository.Count(userId, null, null, null);

            Assert.Single(items, x => x.RequestId.StartsWith(prefix, StringComparison.Ordinal));
            Assert.Equal(1, items.Count(x => x.UserId == userId));
            Assert.Equal(1, total);
            Assert.All(items, item => Assert.Equal(userId, item.UserId));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM usage_logs WHERE request_id LIKE $1");
            cleanup.Parameters.AddWithValue($"{prefix}%");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
