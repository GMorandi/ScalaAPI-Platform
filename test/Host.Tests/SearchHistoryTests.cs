using Npgsql;
using ScalaAPI.Data.Search;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class SearchHistoryTests
{
    [Fact]
    public async Task RecordAndListByUser()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new SearchHistoryStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);
        var apiKeyId = await EnsureTestApiKey(dataSource, userId, suffix);
        var leaseId = $"search-lease-{suffix}";

        var entry = await store.RecordAsync(userId, apiKeyId, leaseId,
            "test query", "example.com", "day",
            3, false, "openai", 1001, "success", null);

        Assert.Equal(userId, entry.UserId);
        Assert.Equal(leaseId, entry.LeaseId);
        Assert.Equal("test query", entry.Query);
        Assert.Equal("example.com", entry.DomainFilter);
        Assert.Equal("day", entry.RecencyFilter);
        Assert.Equal(3, entry.ResultCount);
        Assert.False(entry.Truncated);
        Assert.Equal("openai", entry.ProviderPlatform);
        Assert.Equal("success", entry.Status);

        var history = await store.ListByUserAsync(userId, null, 10);
        Assert.Contains(history, e => e.LeaseId == leaseId);
    }

    [Fact]
    public async Task ListByUserFiltersBySince()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new SearchHistoryStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);
        var apiKeyId = await EnsureTestApiKey(dataSource, userId, suffix);

        await store.RecordAsync(userId, apiKeyId, $"search-since-{suffix}",
            "old query", null, null, 1, false, "openai", 1001, "success", null);

        var future = DateTimeOffset.UtcNow.AddHours(1);
        var filtered = await store.ListByUserAsync(userId, future, 10);
        Assert.DoesNotContain(filtered, e => e.LeaseId == $"search-since-{suffix}");
    }

    [Fact]
    public async Task ListForAuditFiltersByProviderAndStatus()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new SearchHistoryStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);
        var apiKeyId = await EnsureTestApiKey(dataSource, userId, suffix);

        await store.RecordAsync(userId, apiKeyId, $"search-audit-ok-{suffix}",
            "query1", null, null, 2, false, "xai", 2001, "success", null);
        await store.RecordAsync(userId, apiKeyId, $"search-audit-err-{suffix}",
            "query2", null, null, 0, false, "openai", 1002, "error", "rate_limited");

        var xaiEntries = await store.ListForAuditAsync("xai", null, 100);
        Assert.All(xaiEntries, e => Assert.Equal("xai", e.ProviderPlatform));

        var errorEntries = await store.ListForAuditAsync(null, "error", 100);
        Assert.All(errorEntries, e => Assert.Equal("error", e.Status));
    }

    [Fact]
    public async Task DuplicateLeaseIdUpdatesStatus()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new SearchHistoryStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);
        var apiKeyId = await EnsureTestApiKey(dataSource, userId, suffix);
        var leaseId = $"search-idem-{suffix}";

        await store.RecordAsync(userId, apiKeyId, leaseId,
            "initial query", null, null, 0, false, "openai", 1001, "pending", null);

        var updated = await store.RecordAsync(userId, apiKeyId, leaseId,
            "initial query", null, null, 5, false, "openai", 1001, "success", null);

        Assert.Equal("success", updated.Status);
        Assert.Equal(5, updated.ResultCount);
    }

    private static async Task<long> EnsureTestUser(NpgsqlDataSource dataSource, string suffix)
    {
        await using var cmd = dataSource.CreateCommand($"""
            INSERT INTO users (email, password_hash, role, status)
            VALUES ($1, 'test', 'user', 'active')
            ON CONFLICT (email) DO UPDATE SET status = 'active'
            RETURNING id
            """);
        cmd.Parameters.AddWithValue($"search-test-{suffix}@test.local");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<long> EnsureTestApiKey(NpgsqlDataSource dataSource, long userId, string suffix)
    {
        await using var cmd = dataSource.CreateCommand($"""
            INSERT INTO api_keys (user_id, name, key_hash, scopes, status)
            VALUES ($1, $2, $3, $4, 'active')
            ON CONFLICT (key_hash) DO UPDATE SET status = 'active'
            RETURNING id
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue($"search-test-key-{suffix}");
        cmd.Parameters.AddWithValue($"hash-{suffix}");
        cmd.Parameters.AddWithValue("search");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
