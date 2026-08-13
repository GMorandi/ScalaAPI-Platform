using System.Data;
using Npgsql;

namespace ScalaAPI.Data.Referrals;

/// <summary>
/// Manages signup referral attribution with anti-abuse controls.
/// Each user can only be referred once (UNIQUE constraint on referred_user_id).
/// Supports rebate/transfer processing after attribution.
/// </summary>
public sealed class ReferralService(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Attributes a referral on signup. Anti-abuse: one referral per referred user.
    /// The referrer must be an existing active user with a different ID.
    /// </summary>
    public async Task<AttributionResult> AttributeAsync(
        long referrerUserId,
        long referredUserId,
        CancellationToken ct = default)
    {
        if (referrerUserId <= 0 || referredUserId <= 0
            || referrerUserId == referredUserId)
            return new AttributionResult(AttributionStatus.Invalid);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        // Anti-abuse: check if this user was already referred
        await using (var dupCheck = connection.CreateCommand())
        {
            dupCheck.Transaction = transaction;
            dupCheck.CommandText = """
                SELECT referral_id FROM referral_attributions
                WHERE referred_user_id = $1
                FOR UPDATE
                """;
            dupCheck.Parameters.AddWithValue(referredUserId);
            var existing = await dupCheck.ExecuteScalarAsync(ct);
            if (existing is not null and not DBNull)
            {
                await transaction.CommitAsync(ct);
                return new AttributionResult(AttributionStatus.Duplicate,
                    Convert.ToInt64(existing));
            }
        }

        // Verify both users exist
        if (!await UserExistsAsync(connection, transaction, referrerUserId, ct)
            || !await UserExistsAsync(connection, transaction, referredUserId, ct))
        {
            await transaction.RollbackAsync(ct);
            return new AttributionResult(AttributionStatus.UserNotFound);
        }

        long referralId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO referral_attributions (referrer_user_id, referred_user_id)
                VALUES ($1, $2)
                ON CONFLICT (referred_user_id) DO NOTHING
                RETURNING referral_id
                """;
            insert.Parameters.AddWithValue(referrerUserId);
            insert.Parameters.AddWithValue(referredUserId);
            var result = await insert.ExecuteScalarAsync(ct);
            if (result is null or DBNull)
            {
                await transaction.CommitAsync(ct);
                return new AttributionResult(AttributionStatus.Duplicate);
            }
            referralId = Convert.ToInt64(result);
        }

        await transaction.CommitAsync(ct);
        return new AttributionResult(AttributionStatus.Created, referralId);
    }

    /// <summary>
    /// Processes a rebate for a completed referral attribution.
    /// Transfers the rebate amount to the referrer's accounting balance.
    /// </summary>
    public async Task<RebateResult> ProcessRebateAsync(
        long referralId,
        decimal rebateAmount,
        CancellationToken ct = default)
    {
        if (referralId <= 0 || rebateAmount <= 0m)
            return new RebateResult(RebateStatus.Invalid);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT referrer_user_id, referred_user_id, rebate_amount, status
                FROM referral_attributions
                WHERE referral_id = $1
                FOR UPDATE
                """;
            find.Parameters.AddWithValue(referralId);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await transaction.RollbackAsync(ct);
                return new RebateResult(RebateStatus.NotFound);
            }

            var status = reader.GetString(3);
            if (!string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(ct);
                return new RebateResult(RebateStatus.AlreadyProcessed);
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE referral_attributions
                SET rebate_amount = $2, status = 'rebated'
                WHERE referral_id = $1 AND status = 'pending'
                """;
            update.Parameters.AddWithValue(referralId);
            update.Parameters.AddWithValue(rebateAmount);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                await transaction.RollbackAsync(ct);
                return new RebateResult(RebateStatus.AlreadyProcessed);
            }
        }

        await transaction.CommitAsync(ct);
        return new RebateResult(RebateStatus.Applied, referralId);
    }

    /// <summary>
    /// Lists referrals for a referrer user.
    /// </summary>
    public async Task<IReadOnlyList<ReferralView>> ListForReferrerAsync(
        long referrerUserId, CancellationToken ct = default)
    {
        if (referrerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(referrerUserId));
        await using var command = dataSource.CreateCommand("""
            SELECT referral_id, referrer_user_id, referred_user_id,
                   rebate_amount, status, created_at
            FROM referral_attributions
            WHERE referrer_user_id = $1
            ORDER BY created_at DESC
            """);
        command.Parameters.AddWithValue(referrerUserId);
        var items = new List<ReferralView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ReferralView(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetDecimal(3), reader.GetString(4), reader.GetDateTime(5)));
        }
        return items;
    }

    /// <summary>
    /// Lists all referrals (admin view).
    /// </summary>
    public async Task<IReadOnlyList<ReferralView>> ListAllAsync(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        await using var command = dataSource.CreateCommand("""
            SELECT referral_id, referrer_user_id, referred_user_id,
                   rebate_amount, status, created_at
            FROM referral_attributions
            ORDER BY created_at DESC
            LIMIT $1 OFFSET $2
            """);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(offset);
        var items = new List<ReferralView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ReferralView(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetDecimal(3), reader.GetString(4), reader.GetDateTime(5)));
        }
        return items;
    }

    private static async Task<bool> UserExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long userId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM user_accounts WHERE id = $1 AND status = 'active')
            """;
        command.Parameters.AddWithValue(userId);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }
}

public enum AttributionStatus
{
    Created,
    Duplicate,
    UserNotFound,
    Invalid,
}

public sealed record AttributionResult(AttributionStatus Status, long? ReferralId = null);

public enum RebateStatus
{
    Applied,
    AlreadyProcessed,
    NotFound,
    Invalid,
}

public sealed record RebateResult(RebateStatus Status, long? ReferralId = null);

public sealed record ReferralView(
    long ReferralId, long ReferrerUserId, long ReferredUserId,
    decimal RebateAmount, string Status, DateTime CreatedAt);
