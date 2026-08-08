using System.Data;
using System.Text.Json;
using Npgsql;
using ScalaAPI.Data.Accounting;

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
    long Version,
    decimal BalanceAfter);

public sealed class BalanceAdjustmentStore(
    NpgsqlDataSource dataSource,
    AccountingStore accounting)
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

        await AccountingStore.AcquireUserLockAsync(connection, transaction, userId, ct);
        if (!await UserExistsAsync(connection, transaction, userId, ct))
            return new(BalanceAdjustmentStatus.UserNotFound, effectId, null, 0, 0m);

        var activeHolds = await GetActiveHoldsAsync(
            connection, transaction, userId, ct);
        var effect = await accounting.AppendEffectAsync(connection, transaction,
            new AccountingEffect(
                userId, effectId, EntryType, delta,
                IdempotencyKey: idempotencyKey,
                Description: reason,
                CreatedBy: actorId,
                MinimumBalance: activeHolds), ct);

        var status = effect.Status switch
        {
            AccountingEffectStatus.Created => BalanceAdjustmentStatus.Created,
            AccountingEffectStatus.Replay => BalanceAdjustmentStatus.Replay,
            AccountingEffectStatus.Conflict => BalanceAdjustmentStatus.Conflict,
            AccountingEffectStatus.InsufficientFunds => BalanceAdjustmentStatus.InsufficientFunds,
            _ => throw new InvalidOperationException($"Unknown accounting status {effect.Status}"),
        };

        if (status == BalanceAdjustmentStatus.Created)
        {
            await using var audit = connection.CreateCommand();
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
                ledger_version = effect.Snapshot.Version,
                balance_after = effect.Snapshot.Balance,
            }));
            await audit.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new(status, effectId, effect.LedgerId,
            effect.Snapshot.Version, effect.Snapshot.Balance);
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
