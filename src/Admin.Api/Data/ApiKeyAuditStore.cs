using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data;

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
}
