using Npgsql;

namespace ScalaAPI.Data.Search;

public sealed record SearchHistoryEntry(
    long Id,
    long UserId,
    long ApiKeyId,
    string LeaseId,
    string Query,
    string? DomainFilter,
    string? RecencyFilter,
    int ResultCount,
    bool Truncated,
    string ProviderPlatform,
    long ProviderAccountId,
    string Status,
    string? ErrorCode,
    DateTimeOffset CreatedAt);

public interface ISearchHistoryStore
{
    Task<SearchHistoryEntry> RecordAsync(long userId, long apiKeyId, string leaseId,
        string query, string? domainFilter, string? recencyFilter,
        int resultCount, bool truncated, string providerPlatform,
        long providerAccountId, string status, string? errorCode,
        CancellationToken ct = default);

    Task<IReadOnlyList<SearchHistoryEntry>> ListByUserAsync(long userId,
        DateTimeOffset? since, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<SearchHistoryEntry>> ListForAuditAsync(string? providerPlatform,
        string? status, int limit, CancellationToken ct = default);
}

public sealed class SearchHistoryStore(NpgsqlDataSource dataSource) : ISearchHistoryStore
{
    public async Task<SearchHistoryEntry> RecordAsync(long userId, long apiKeyId,
        string leaseId, string query, string? domainFilter, string? recencyFilter,
        int resultCount, bool truncated, string providerPlatform,
        long providerAccountId, string status, string? errorCode,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO search_history
                (user_id, api_key_id, lease_id, query, domain_filter, recency_filter,
                 result_count, truncated, provider_platform, provider_account_id,
                 status, error_code)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (lease_id) DO UPDATE SET
                result_count = EXCLUDED.result_count,
                truncated = EXCLUDED.truncated,
                status = EXCLUDED.status,
                error_code = EXCLUDED.error_code
            RETURNING id, user_id, api_key_id, lease_id, query, domain_filter,
                      recency_filter, result_count, truncated, provider_platform,
                      provider_account_id, status, error_code, created_at
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(leaseId);
        command.Parameters.AddWithValue(query);
        command.Parameters.AddWithValue(domainFilter ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(recencyFilter ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(resultCount);
        command.Parameters.AddWithValue(truncated);
        command.Parameters.AddWithValue(providerPlatform);
        command.Parameters.AddWithValue(providerAccountId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(errorCode ?? (object)DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadEntry(reader);
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> ListByUserAsync(long userId,
        DateTimeOffset? since, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var command = dataSource.CreateCommand("""
            SELECT id, user_id, api_key_id, lease_id, query, domain_filter,
                   recency_filter, result_count, truncated, provider_platform,
                   provider_account_id, status, error_code, created_at
            FROM search_history
            WHERE user_id = $1 AND ($2::timestamptz IS NULL OR created_at >= $2)
            ORDER BY created_at DESC
            LIMIT $3
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);
        command.Parameters.AddWithValue(limit);

        return await ReadListAsync(command, ct);
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> ListForAuditAsync(
        string? providerPlatform, string? status, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var filters = new List<string>();
        var paramIndex = 0;
        await using var command = dataSource.CreateCommand();
        if (!string.IsNullOrWhiteSpace(providerPlatform))
        {
            paramIndex++;
            filters.Add($"provider_platform = ${paramIndex}");
            command.Parameters.AddWithValue(providerPlatform);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            paramIndex++;
            filters.Add($"status = ${paramIndex}");
            command.Parameters.AddWithValue(status);
        }
        paramIndex++;
        command.Parameters.AddWithValue(limit);

        var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
        command.CommandText = $"""
            SELECT id, user_id, api_key_id, lease_id, query, domain_filter,
                   recency_filter, result_count, truncated, provider_platform,
                   provider_account_id, status, error_code, created_at
            FROM search_history {where}
            ORDER BY created_at DESC
            LIMIT ${paramIndex}
            """;

        return await ReadListAsync(command, ct);
    }

    private static async Task<IReadOnlyList<SearchHistoryEntry>> ReadListAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        var entries = new List<SearchHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(ReadEntry(reader));
        return entries;
    }

    private static SearchHistoryEntry ReadEntry(NpgsqlDataReader reader)
    {
        return new SearchHistoryEntry(
            Id: reader.GetInt64(0),
            UserId: reader.GetInt64(1),
            ApiKeyId: reader.GetInt64(2),
            LeaseId: reader.GetString(3),
            Query: reader.GetString(4),
            DomainFilter: reader.IsDBNull(5) ? null : reader.GetString(5),
            RecencyFilter: reader.IsDBNull(6) ? null : reader.GetString(6),
            ResultCount: reader.GetInt32(7),
            Truncated: reader.GetBoolean(8),
            ProviderPlatform: reader.GetString(9),
            ProviderAccountId: reader.GetInt64(10),
            Status: reader.GetString(11),
            ErrorCode: reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(13));
    }
}
