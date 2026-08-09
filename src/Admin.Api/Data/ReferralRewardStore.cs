using System.Data;
using System.Text.Json;
using Npgsql;
using ScalaAPI.Data.Accounting;

namespace ScalaAPI.Admin.Data;

public enum ReferralRewardStatus
{
    Created,
    Replay,
    Conflict,
    UserNotFound,
    CodeNotFound,
    Invalid,
}

public sealed record ReferralRewardResult(
    ReferralRewardStatus Status,
    long? RecordId,
    long? LedgerId,
    AccountingSnapshot Snapshot);

/// <summary>
/// Applies an administrator-approved referral reward as one durable business
/// command. The referral row, accounting credit, aggregate counters, and audit
/// evidence commit together or not at all.
/// </summary>
public sealed class ReferralRewardStore(
    NpgsqlDataSource dataSource,
    AccountingStore accounting)
{
    public const string EntryType = "referral_bonus";

    public async Task<ReferralRewardResult> RecordAsync(
        long actorId,
        long referrerUserId,
        long referredUserId,
        decimal bonusUsd,
        string idempotencyKey,
        string reason,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (actorId <= 0 || referrerUserId <= 0 || referredUserId <= 0
            || referrerUserId == referredUserId
            || bonusUsd <= 0m || decimal.Round(bonusUsd, 2) != bonusUsd
            || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            return Empty(ReferralRewardStatus.Invalid);

        var effectId = $"referral:{referrerUserId}:{referredUserId}";
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        var firstUser = Math.Min(referrerUserId, referredUserId);
        var secondUser = Math.Max(referrerUserId, referredUserId);
        await AccountingStore.AcquireUserLockAsync(connection, transaction, firstUser, ct);
        await AccountingStore.AcquireUserLockAsync(connection, transaction, secondUser, ct);

        if (!await UserExistsAsync(connection, transaction, referrerUserId, ct)
            || !await UserExistsAsync(connection, transaction, referredUserId, ct))
            return Empty(ReferralRewardStatus.UserNotFound);

        if (!await ReferralCodeExistsAsync(connection, transaction, referrerUserId, ct))
            return Empty(ReferralRewardStatus.CodeNotFound);

        var existing = await FindRecordAsync(
            connection, transaction, referrerUserId, referredUserId, ct);
        var effect = await accounting.AppendEffectAsync(connection, transaction,
            new AccountingEffect(
                referrerUserId, effectId, EntryType, bonusUsd,
                IdempotencyKey: idempotencyKey,
                Description: reason,
                CreatedBy: actorId), ct);

        if (existing is not null)
        {
            // A pre-existing attribution is terminal. A matching accounting
            // effect is a replay; any other payload is a conflict. Never issue
            // a second credit merely because a command used another key.
            if (existing.Value.BonusUsd != bonusUsd
                || effect.Status != AccountingEffectStatus.Replay)
            {
                await transaction.RollbackAsync(ct);
                return Empty(ReferralRewardStatus.Conflict);
            }

            await transaction.CommitAsync(ct);
            return new(ReferralRewardStatus.Replay, existing.Value.RecordId,
                effect.LedgerId, effect.Snapshot);
        }

        if (effect.Status != AccountingEffectStatus.Created)
        {
            await transaction.RollbackAsync(ct);
            return Empty(effect.Status == AccountingEffectStatus.Conflict
                ? ReferralRewardStatus.Conflict : ReferralRewardStatus.Invalid);
        }

        long recordId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO referral_records(
                    referrer_user_id, referred_user_id, bonus_usd)
                VALUES ($1, $2, $3)
                RETURNING id
                """;
            insert.Parameters.AddWithValue(referrerUserId);
            insert.Parameters.AddWithValue(referredUserId);
            insert.Parameters.AddWithValue(bonusUsd);
            recordId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE referral_codes
                SET total_referrals = total_referrals + 1,
                    total_bonus_usd = total_bonus_usd + $2
                WHERE user_id = $1
                """;
            update.Parameters.AddWithValue(referrerUserId);
            update.Parameters.AddWithValue(bonusUsd);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Referral code disappeared while rewarding");
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit_logs(
                    user_id, action, resource_type, resource_id, details, ip_address)
                VALUES ($1, 'referral.reward', 'referral', $2, $3, $4)
                """;
            audit.Parameters.AddWithValue(actorId);
            audit.Parameters.AddWithValue(recordId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue(JsonSerializer.Serialize(new
            {
                effect_id = effectId,
                referrer_user_id = referrerUserId,
                referred_user_id = referredUserId,
                bonus_usd = bonusUsd,
                idempotency_key = idempotencyKey,
                reason,
                ledger_version = effect.Snapshot.Version,
                balance_after = effect.Snapshot.Balance,
            }));
            audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new(ReferralRewardStatus.Created, recordId, effect.LedgerId,
            effect.Snapshot);
    }

    private static ReferralRewardResult Empty(ReferralRewardStatus status) =>
        new(status, null, null, new AccountingSnapshot(0, 0, 0m));

    private static async Task<bool> UserExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long userId, CancellationToken ct)
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

    private static async Task<bool> ReferralCodeExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long userId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM referral_codes WHERE user_id = $1 FOR UPDATE)";
        command.Parameters.AddWithValue(userId);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task<(long RecordId, decimal BonusUsd)?> FindRecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long referrerUserId, long referredUserId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, bonus_usd
            FROM referral_records
            WHERE referrer_user_id = $1 AND referred_user_id = $2
            FOR UPDATE
            """;
        command.Parameters.AddWithValue(referrerUserId);
        command.Parameters.AddWithValue(referredUserId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetInt64(0), reader.GetDecimal(1))
            : null;
    }
}
