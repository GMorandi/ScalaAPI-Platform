using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ScalaAPI.Data.Redemptions;

/// <summary>
/// Manages redemption code lifecycle with concurrency control, expiry checking,
/// usage limit enforcement, and atomic redemption with duplicate prevention.
/// Codes are stored as hashes; the plaintext code is only known at creation time.
/// </summary>
public sealed class RedemptionService(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Creates a new redemption code. Returns the plaintext code (only shown once).
    /// </summary>
    public async Task<CodeCreationResult> CreateCodeAsync(
        string planId,
        int maxUses,
        TimeSpan? validity,
        string? promotionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planId) || maxUses < 1)
            return new CodeCreationResult(RedemptionCodeStatus.Invalid);

        var codeId = Guid.NewGuid().ToString("N")[..16];
        var plaintext = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var codeHash = HashCode(plaintext);
        var expiresAt = validity.HasValue ? DateTime.UtcNow.Add(validity.Value) : (DateTime?)null;

        await using var command = dataSource.CreateCommand("""
            INSERT INTO redemption_codes (code_id, code_hash, plan_id, max_uses, expires_at, promotion_id)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (code_id) DO NOTHING
            """);
        command.Parameters.AddWithValue(codeId);
        command.Parameters.AddWithValue(codeHash);
        command.Parameters.AddWithValue(planId);
        command.Parameters.AddWithValue(maxUses);
        command.Parameters.AddWithValue((object?)expiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)promotionId ?? DBNull.Value);

        if (await command.ExecuteNonQueryAsync(ct) != 1)
            return new CodeCreationResult(RedemptionCodeStatus.Duplicate);

        return new CodeCreationResult(RedemptionCodeStatus.Created, codeId, plaintext);
    }

    /// <summary>
    /// Redeems a code for a user. Atomic: uses SELECT FOR UPDATE for concurrency control,
    /// checks expiry and usage limits, and prevents duplicate redemptions via UNIQUE constraint.
    /// Only one entitlement is produced per (code, user) pair.
    /// </summary>
    public async Task<RedemptionResult> RedeemAsync(
        string plaintextCode,
        long userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextCode) || userId <= 0)
            return new RedemptionResult(RedemptionStatus.Invalid);

        var codeHash = HashCode(plaintextCode.Trim());

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        // Lock the code row for concurrency control
        await using (var findCode = connection.CreateCommand())
        {
            findCode.Transaction = transaction;
            findCode.CommandText = """
                SELECT code_id, plan_id, max_uses, current_uses, expires_at
                FROM redemption_codes
                WHERE code_hash = $1
                FOR UPDATE
                """;
            findCode.Parameters.AddWithValue(codeHash);
            await using var reader = await findCode.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await transaction.RollbackAsync(ct);
                return new RedemptionResult(RedemptionStatus.CodeNotFound);
            }

            var codeId = reader.GetString(0);
            var planId = reader.GetString(1);
            var maxUses = reader.GetInt32(2);
            var currentUses = reader.GetInt32(3);
            var expiresAt = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
            await reader.DisposeAsync();

            // Check expiry
            if (expiresAt.HasValue && expiresAt.Value <= DateTime.UtcNow)
            {
                await transaction.RollbackAsync(ct);
                return new RedemptionResult(RedemptionStatus.Expired);
            }

            // Check usage limit
            if (currentUses >= maxUses)
            {
                await transaction.RollbackAsync(ct);
                return new RedemptionResult(RedemptionStatus.UsageLimitReached);
            }

            // Attempt atomic insertion (UNIQUE constraint prevents duplicates)
            await using (var insertHistory = connection.CreateCommand())
            {
                insertHistory.Transaction = transaction;
                insertHistory.CommandText = """
                    INSERT INTO redemption_history (code_id, user_id)
                    VALUES ($1, $2)
                    ON CONFLICT (code_id, user_id) DO NOTHING
                    RETURNING redemption_id
                    """;
                insertHistory.Parameters.AddWithValue(codeId);
                insertHistory.Parameters.AddWithValue(userId);
                var result = await insertHistory.ExecuteScalarAsync(ct);
                if (result is null or DBNull)
                {
                    await transaction.CommitAsync(ct);
                    return new RedemptionResult(RedemptionStatus.Duplicate);
                }
            }

            // Increment usage counter (quota reservation matches usage settlement)
            await using (var increment = connection.CreateCommand())
            {
                increment.Transaction = transaction;
                increment.CommandText = """
                    UPDATE redemption_codes
                    SET current_uses = current_uses + 1
                    WHERE code_id = $1 AND current_uses < max_uses
                    """;
                increment.Parameters.AddWithValue(codeId);
                var affected = await increment.ExecuteNonQueryAsync(ct);
                if (affected != 1)
                {
                    // Race condition: another concurrent redemption consumed the last slot
                    await transaction.RollbackAsync(ct);
                    return new RedemptionResult(RedemptionStatus.UsageLimitReached);
                }
            }

            await transaction.CommitAsync(ct);
            return new RedemptionResult(RedemptionStatus.Redeemed, PlanId: planId);
        }
    }

    /// <summary>
    /// Lists redemption history for a user.
    /// </summary>
    public async Task<IReadOnlyList<RedemptionView>> ListForUserAsync(
        long userId, CancellationToken ct = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        await using var command = dataSource.CreateCommand("""
            SELECT rh.redemption_id, rh.code_id, rc.plan_id, rh.redeemed_at,
                   rc.promotion_id
            FROM redemption_history rh
            JOIN redemption_codes rc ON rc.code_id = rh.code_id
            WHERE rh.user_id = $1
            ORDER BY rh.redeemed_at DESC
            """);
        command.Parameters.AddWithValue(userId);
        var items = new List<RedemptionView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new RedemptionView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return items;
    }

    private static string HashCode(string plaintext)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record CodeCreationResult(
    RedemptionCodeStatus Status,
    string? CodeId = null,
    string? PlaintextCode = null);

public enum RedemptionCodeStatus
{
    Created,
    Duplicate,
    Invalid,
}

public enum RedemptionStatus
{
    Redeemed,
    Duplicate,
    CodeNotFound,
    Expired,
    UsageLimitReached,
    Invalid,
}

public sealed record RedemptionResult(
    RedemptionStatus Status,
    long? RedemptionId = null,
    string? PlanId = null);

public sealed record RedemptionView(
    long RedemptionId, string CodeId, string PlanId,
    DateTime RedeemedAt, string? PromotionId);
