using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data;

public sealed record ApiKeyAuditEvent(
    long Id,
    long ApiKeyId,
    long UserId,
    long ActorUserId,
    string Action,
    string[] Scopes,
    long? ExpiresAtMs,
    string? Capability,
    string? Reason,
    string? RequestId,
    DateTime CreatedAt);

public sealed record ApiKeyAuditPage(
    IReadOnlyList<ApiKeyAuditEvent> Items,
    long Total,
    int Page,
    int Size);

public sealed class ApiKeyAuditStore(NpgsqlDataSource dataSource)
{
    public async Task RecordAsync(
        long apiKeyId, long userId, long actorUserId, string action,
        string[] scopes, long? expiresAtMs, string? capability = null,
        string? reason = null, string? requestId = null,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO api_key_audit_events
                (api_key_id, user_id, actor_user_id, action, scopes,
                 expires_at_ms, capability, reason, request_id)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::bigint, $7, $8, $9)
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(actorUserId);
        command.Parameters.AddWithValue(action);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(scopes));
        command.Parameters.AddWithValue((object?)expiresAtMs ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)capability ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)requestId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ApiKeyAuditPage> ListAsync(
        long apiKeyId,
        string? action,
        DateTime? from,
        DateTime? to,
        int page,
        int size,
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 10_000);
        size = Math.Clamp(size, 1, 100);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var count = new NpgsqlCommand("""
            SELECT count(*)
            FROM api_key_audit_events
            WHERE api_key_id = $1
              AND ($2::text IS NULL OR action = $2)
              AND ($3::timestamptz IS NULL OR created_at >= $3)
              AND ($4::timestamptz IS NULL OR created_at <= $4)
            """, connection);
        count.Parameters.AddWithValue(apiKeyId);
        count.Parameters.AddWithValue((object?)action ?? DBNull.Value);
        count.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        count.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        var total = (long)(await count.ExecuteScalarAsync(ct) ?? 0L);

        await using var query = new NpgsqlCommand("""
            SELECT id, api_key_id, user_id, actor_user_id, action, scopes,
                   expires_at_ms, capability, reason, request_id, created_at
            FROM api_key_audit_events
            WHERE api_key_id = $1
              AND ($2::text IS NULL OR action = $2)
              AND ($3::timestamptz IS NULL OR created_at >= $3)
              AND ($4::timestamptz IS NULL OR created_at <= $4)
            ORDER BY created_at DESC, id DESC
            OFFSET $5 LIMIT $6
            """, connection);
        query.Parameters.AddWithValue(apiKeyId);
        query.Parameters.AddWithValue((object?)action ?? DBNull.Value);
        query.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        query.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        query.Parameters.AddWithValue((page - 1) * size);
        query.Parameters.AddWithValue(size);

        var items = new List<ApiKeyAuditEvent>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var scopesJson = reader.GetString(5);
            var scopes = JsonSerializer.Deserialize<string[]>(scopesJson) ?? [];
            items.Add(new ApiKeyAuditEvent(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetString(4), scopes,
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetDateTime(10)));
        }

        return new ApiKeyAuditPage(items, total, page, size);
    }
}
