using Npgsql;

namespace ScalaAPI.Data.Provider;

public sealed record ProviderCredentialRefreshAuditEntry(
    long Id, Guid AttemptId, long AccountId, string Source,
    int VersionBefore, int? VersionAfter, string Outcome, string? ErrorCode,
    string TokenEndpointHost, DateTime StartedAt, DateTime CompletedAt,
    int DurationMilliseconds);

public sealed class ProviderCredentialRefreshAuditStore(NpgsqlDataSource dataSource)
{
    public async Task RecordAsync(
        Guid attemptId, long accountId, string source, int versionBefore,
        int? versionAfter, string outcome, string? errorCode,
        string tokenEndpointHost, DateTime startedAt, DateTime completedAt,
        int durationMilliseconds, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO provider_credential_refresh_attempts
                (attempt_id, account_id, source, version_before, version_after,
                 outcome, error_code, token_endpoint_host, started_at, completed_at,
                 duration_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT (attempt_id) DO NOTHING
            """);
        command.Parameters.AddWithValue(attemptId);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(source);
        command.Parameters.AddWithValue(versionBefore);
        command.Parameters.AddWithValue((object?)versionAfter ?? DBNull.Value);
        command.Parameters.AddWithValue(outcome);
        command.Parameters.AddWithValue((object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue(tokenEndpointHost);
        command.Parameters.AddWithValue(startedAt);
        command.Parameters.AddWithValue(completedAt);
        command.Parameters.AddWithValue(durationMilliseconds);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderCredentialRefreshAuditEntry>> ListAsync(
        long accountId, int page, int size, string? outcome = null,
        string? source = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 200);
        var filters = new List<string> { "account_id = $1" };
        await using var command = dataSource.CreateCommand();
        command.Parameters.AddWithValue(accountId);
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            filters.Add($"outcome = ${command.Parameters.Count + 1}");
            command.Parameters.AddWithValue(outcome.Trim());
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            filters.Add($"source = ${command.Parameters.Count + 1}");
            command.Parameters.AddWithValue(source.Trim());
        }
        var limit = command.Parameters.Count + 1;
        var offset = command.Parameters.Count + 2;
        command.CommandText = $"""
            SELECT id, attempt_id, account_id, source, version_before,
                   version_after, outcome, error_code, token_endpoint_host,
                   started_at, completed_at, duration_ms
            FROM provider_credential_refresh_attempts
            WHERE {string.Join(" AND ", filters)}
            ORDER BY completed_at DESC, id DESC
            LIMIT ${limit} OFFSET ${offset}
            """;
        command.Parameters.AddWithValue(size);
        command.Parameters.AddWithValue((page - 1) * size);

        var entries = new List<ProviderCredentialRefreshAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
                reader.GetDateTime(9), reader.GetDateTime(10), reader.GetInt32(11)));
        }
        return entries;
    }

    public async Task<long> CountAsync(long accountId, string? outcome = null,
        string? source = null, CancellationToken ct = default)
    {
        var filters = new List<string> { "account_id = $1" };
        await using var command = dataSource.CreateCommand();
        command.Parameters.AddWithValue(accountId);
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            filters.Add($"outcome = ${command.Parameters.Count + 1}");
            command.Parameters.AddWithValue(outcome.Trim());
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            filters.Add($"source = ${command.Parameters.Count + 1}");
            command.Parameters.AddWithValue(source.Trim());
        }
        command.CommandText = $"SELECT count(*) FROM provider_credential_refresh_attempts WHERE {string.Join(" AND ", filters)}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
