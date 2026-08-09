using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data;

public sealed record ChannelCheckRequest(
    long AccountId,
    string? Status,
    int LatencyMs,
    string? Error);

public sealed record ChannelMonitorView(
    long Id,
    long AccountId,
    string Status,
    int LatencyMs,
    string? LastError,
    DateTime CheckedAt);

public enum ChannelMonitorWriteStatus
{
    Created,
    AccountNotFound,
    Invalid,
}

public sealed record ChannelMonitorWriteResult(
    ChannelMonitorWriteStatus Status,
    long? Id = null,
    string? Error = null);

public sealed class ChannelMonitorStore(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<ChannelMonitorView>> ListAsync(
        long? accountId,
        int page,
        int size,
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 10_000);
        size = Math.Clamp(size, 1, 100);
        await using var command = dataSource.CreateCommand("""
            SELECT id, account_id, status, latency_ms, last_error, checked_at
            FROM channel_monitors
            WHERE ($1::bigint IS NULL OR account_id = $1)
            ORDER BY checked_at DESC, id DESC
            OFFSET $2 LIMIT $3
            """);
        command.Parameters.AddWithValue((object?)accountId ?? DBNull.Value);
        command.Parameters.AddWithValue((page - 1) * size);
        command.Parameters.AddWithValue(size);
        var items = new List<ChannelMonitorView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ChannelMonitorView(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDateTime(5)));
        }
        return items;
    }

    public async Task<ChannelMonitorWriteResult> RecordAsync(
        long actorId,
        ChannelCheckRequest request,
        string? clientIp,
        CancellationToken ct = default)
    {
        var status = request.Status?.Trim().ToLowerInvariant();
        var error = string.IsNullOrWhiteSpace(request.Error) ? null : request.Error.Trim();
        if (actorId <= 0 || request.AccountId <= 0)
            return new(ChannelMonitorWriteStatus.Invalid, Error: "actor_id and account_id must be positive");
        if (status is not ("healthy" or "degraded" or "unreachable" or "unknown"))
            return new(ChannelMonitorWriteStatus.Invalid, Error: "invalid monitor status");
        if (request.LatencyMs is < 0 or > 600_000)
            return new(ChannelMonitorWriteStatus.Invalid, Error: "latency_ms must be 0-600000");
        if (error is not null && (error.Length > 1_000 || error.Any(char.IsControl)))
            return new(ChannelMonitorWriteStatus.Invalid, Error: "error is too long or contains control characters");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var account = connection.CreateCommand();
        account.Transaction = transaction;
        account.CommandText = """
            SELECT 1 FROM accounts
            WHERE id = $1 AND status = 'active'
            FOR KEY SHARE
            """;
        account.Parameters.AddWithValue(request.AccountId);
        if (await account.ExecuteScalarAsync(ct) is null)
        {
            await transaction.RollbackAsync(ct);
            return new(ChannelMonitorWriteStatus.AccountNotFound);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO channel_monitors(account_id, status, latency_ms, last_error)
            VALUES ($1, $2, $3, $4)
            RETURNING id
            """;
        insert.Parameters.AddWithValue(request.AccountId);
        insert.Parameters.AddWithValue(status);
        insert.Parameters.AddWithValue(request.LatencyMs);
        insert.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));

        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(
                user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, 'channel_monitor.checked', 'account', $2, $3, $4)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(request.AccountId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue(JsonSerializer.Serialize(new
        {
            monitor_id = id,
            status,
            latency_ms = request.LatencyMs,
            has_error = error is not null,
        }));
        audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(ChannelMonitorWriteStatus.Created, id);
    }
}
