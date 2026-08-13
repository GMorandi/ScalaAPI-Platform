using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data.Audit;

/// <summary>
/// Immutable, append-only audit log service with export authorization and retention enforcement.
/// Backed by the audit_log_immutable table which has triggers preventing UPDATE/DELETE.
/// </summary>
public sealed class AuditLogService
{
    private readonly NpgsqlDataSource _dataSource;

    public AuditLogService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public record ImmutableAuditEntry(
        long LogId,
        string EventType,
        long? ActorUserId,
        string? ActorIp,
        string? ResourceType,
        string? ResourceId,
        string Action,
        string Result,
        string? Details,
        DateTime CreatedAt);

    public record AuditPage(IReadOnlyList<ImmutableAuditEntry> Items, long Total, int Page, int Size);

    /// <summary>
    /// Append an entry. This is the only mutation allowed on the immutable log.
    /// </summary>
    public async Task AppendAsync(string eventType, string action, string result,
        long? actorUserId = null, string? actorIp = null,
        string? resourceType = null, string? resourceId = null,
        string? details = null, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("""
            INSERT INTO audit_log_immutable
                (event_type, actor_user_id, actor_ip, resource_type, resource_id, action, result, details)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)
            """);
        cmd.Parameters.AddWithValue(eventType);
        cmd.Parameters.AddWithValue((object?)actorUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)actorIp ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)resourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)resourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.AddWithValue(result);
        cmd.Parameters.AddWithValue((object?)details ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Query the immutable log (read-only export). Requires admin authorization at the endpoint level.
    /// </summary>
    public async Task<AuditPage> ExportAsync(
        string? eventType, string? action, DateTime? from, DateTime? to,
        int page, int size, CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 10_000);
        size = Math.Clamp(size, 1, 1_000);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = """
            SELECT count(*) FROM audit_log_immutable
            WHERE ($1::text IS NULL OR event_type = $1)
              AND ($2::text IS NULL OR action = $2)
              AND ($3::timestamptz IS NULL OR created_at >= $3)
              AND ($4::timestamptz IS NULL OR created_at <= $4)
            """;
        countCmd.Parameters.AddWithValue((object?)eventType ?? DBNull.Value);
        countCmd.Parameters.AddWithValue((object?)action ?? DBNull.Value);
        countCmd.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        countCmd.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

        await using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = """
            SELECT log_id, event_type, actor_user_id, actor_ip, resource_type, resource_id,
                   action, result, details::text, created_at
            FROM audit_log_immutable
            WHERE ($1::text IS NULL OR event_type = $1)
              AND ($2::text IS NULL OR action = $2)
              AND ($3::timestamptz IS NULL OR created_at >= $3)
              AND ($4::timestamptz IS NULL OR created_at <= $4)
            ORDER BY created_at DESC, log_id DESC
            OFFSET $5 LIMIT $6
            """;
        queryCmd.Parameters.AddWithValue((object?)eventType ?? DBNull.Value);
        queryCmd.Parameters.AddWithValue((object?)action ?? DBNull.Value);
        queryCmd.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        queryCmd.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        queryCmd.Parameters.AddWithValue((page - 1) * size);
        queryCmd.Parameters.AddWithValue(size);

        var items = new List<ImmutableAuditEntry>();
        await using var reader = await queryCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ImmutableAuditEntry(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetDateTime(9)));
        }
        return new AuditPage(items, total, page, size);
    }

    /// <summary>
    /// Enforce retention policy by deleting entries older than the retention period.
    /// Note: This operates on the mutable audit_logs table (not the immutable one).
    /// The immutable table is protected by triggers and cannot be purged.
    /// </summary>
    public async Task<int> EnforceRetentionAsync(TimeSpan retentionPeriod, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;
        await using var cmd = _dataSource.CreateCommand("""
            DELETE FROM audit_logs WHERE created_at < $1
            """);
        cmd.Parameters.AddWithValue(cutoff);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
