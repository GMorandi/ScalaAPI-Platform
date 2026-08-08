using System.Data;
using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data;

public enum BalanceAdjustmentStatus
{
    Created,
    Replay,
    Conflict,
    UserNotFound,
    InsufficientFunds,
}

public sealed record BalanceAdjustmentResult(
    BalanceAdjustmentStatus Status,
    string EffectId,
    long? LedgerId,
    decimal BalanceAfter);

public sealed class BalanceAdjustmentStore(NpgsqlDataSource dataSource)
{
    public const string EntryType = "admin_adjustment";

    public async Task<BalanceAdjustmentResult> RecordAsync(
        long userId,
        long actorId,
        string idempotencyKey,
        decimal delta,
        string reason,
        CancellationToken ct = default)
    {
        var effectId = $"admin-adjustment:{userId}:{idempotencyKey}";
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        await using (var userLock = connection.CreateCommand())
        {
            userLock.Transaction = transaction;
            userLock.CommandText = "SELECT pg_advisory_xact_lock($1)";
            userLock.Parameters.AddWithValue(userId);
            await userLock.ExecuteNonQueryAsync(ct);
        }

        if (!await UserExistsAsync(connection, transaction, userId, ct))
            return new(BalanceAdjustmentStatus.UserNotFound, effectId, null, 0m);

        var existing = await FindExistingAsync(
            connection, transaction, userId, idempotencyKey, ct);
        if (existing is not null)
        {
            var currentBalance = await GetLedgerBalanceAsync(
                connection, transaction, userId, ct);
            await transaction.CommitAsync(ct);
            var status = existing.Value.Amount == delta
                && string.Equals(existing.Value.Description, reason, StringComparison.Ordinal)
                ? BalanceAdjustmentStatus.Replay
                : BalanceAdjustmentStatus.Conflict;
            return new(status, effectId, existing.Value.Id, currentBalance);
        }

        var ledgerBalance = await GetLedgerBalanceAsync(
            connection, transaction, userId, ct);
        var activeHolds = await GetActiveHoldsAsync(
            connection, transaction, userId, ct);
        var balanceAfter = ledgerBalance + delta;
        if (balanceAfter < activeHolds)
            return new(BalanceAdjustmentStatus.InsufficientFunds,
                effectId, null, ledgerBalance);

        long ledgerId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO balance_ledger(
                    user_id, reference, amount, entry_type,
                    idempotency_key, description, created_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                RETURNING id
                """;
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(effectId);
            insert.Parameters.AddWithValue(delta);
            insert.Parameters.AddWithValue(EntryType);
            insert.Parameters.AddWithValue(idempotencyKey);
            insert.Parameters.AddWithValue(reason);
            insert.Parameters.AddWithValue(actorId);
            ledgerId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit_logs(
                    user_id, action, resource_type, resource_id, details)
                VALUES ($1, 'balance.adjust', 'user', $2, $3)
                """;
            audit.Parameters.AddWithValue(actorId);
            audit.Parameters.AddWithValue(userId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue(JsonSerializer.Serialize(new
            {
                effect_id = effectId,
                delta,
                reason,
                balance_after = balanceAfter,
            }));
            await audit.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new(BalanceAdjustmentStatus.Created, effectId, ledgerId, balanceAfter);
    }

    private static async Task<bool> UserExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM entity_registry
                WHERE entity_type = 'user' AND entity_id = $1 AND status = 'active')
            """;
        command.Parameters.AddWithValue(userId);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task<(long Id, decimal Amount, string Description)?> FindExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        string idempotencyKey,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, amount, description
            FROM balance_ledger
            WHERE user_id = $1 AND idempotency_key = $2 AND entry_type = $3
            """;
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(idempotencyKey);
        command.Parameters.AddWithValue(EntryType);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt64(0), reader.GetDecimal(1), reader.GetString(2));
    }

    private static async Task<decimal> GetLedgerBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COALESCE(sum(amount), 0) FROM balance_ledger WHERE user_id = $1";
        command.Parameters.AddWithValue(userId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<decimal> GetActiveHoldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(sum(amount), 0)
            FROM balance_holds
            WHERE user_id = $1 AND status = 'active'
            """;
        command.Parameters.AddWithValue(userId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(ct));
    }
}
