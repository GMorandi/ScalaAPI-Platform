using System.Data;
using Npgsql;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed class SlotLeaseStore(
    NpgsqlDataSource dataSource,
    ILogger<SlotLeaseStore> logger) : ISlotLeaseStore
{
    public async Task<bool> TryAcquireAccountSlot(long accountId, string leaseToken,
        string requestId, string siloId, DateTime expiresAt, int maxConcurrency,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Ensure the slot row exists (upsert)
        await using (var ensureCmd = new NpgsqlCommand("""
            INSERT INTO account_concurrency_slots (account_id, max_concurrency)
            VALUES (@accountId, @maxConcurrency)
            ON CONFLICT (account_id) DO UPDATE
                SET max_concurrency = EXCLUDED.max_concurrency,
                    updated_at = now()
            """, conn) { Transaction = tx })
        {
            ensureCmd.Parameters.AddWithValue("accountId", accountId);
            ensureCmd.Parameters.AddWithValue("maxConcurrency", maxConcurrency);
            await ensureCmd.ExecuteNonQueryAsync(ct);
        }

        // Lock the row for atomic read-modify-write
        long generation;
        int activeCount;
        await using (var lockCmd = new NpgsqlCommand("""
            SELECT generation, active_count FROM account_concurrency_slots
            WHERE account_id = @accountId FOR UPDATE
            """, conn) { Transaction = tx })
        {
            lockCmd.Parameters.AddWithValue("accountId", accountId);
            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false;
            generation = reader.GetInt64(0);
            activeCount = reader.GetInt32(1);
        }

        // Reclaim expired leases
        int reclaimed;
        await using (var reclaimCmd = new NpgsqlCommand("""
            UPDATE account_slot_leases
            SET status = 'expired', released_at = now()
            WHERE account_id = @accountId AND status = 'active' AND expires_at <= now()
            """, conn) { Transaction = tx })
        {
            reclaimCmd.Parameters.AddWithValue("accountId", accountId);
            reclaimed = await reclaimCmd.ExecuteNonQueryAsync(ct);
        }

        if (reclaimed > 0)
        {
            // Recount active leases
            await using var recountCmd = new NpgsqlCommand("""
                SELECT COUNT(*)::int FROM account_slot_leases
                WHERE account_id = @accountId AND status = 'active'
                """, conn) { Transaction = tx };
            recountCmd.Parameters.AddWithValue("accountId", accountId);
            activeCount = (int)(await recountCmd.ExecuteScalarAsync(ct))!;

            await using var updateCountCmd = new NpgsqlCommand("""
                UPDATE account_concurrency_slots
                SET active_count = @count, updated_at = now()
                WHERE account_id = @accountId
                """, conn) { Transaction = tx };
            updateCountCmd.Parameters.AddWithValue("accountId", accountId);
            updateCountCmd.Parameters.AddWithValue("count", activeCount);
            await updateCountCmd.ExecuteNonQueryAsync(ct);
        }

        // Check capacity
        if (activeCount >= maxConcurrency)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // Insert lease
        await using (var insertCmd = new NpgsqlCommand("""
            INSERT INTO account_slot_leases
                (lease_token, account_id, request_id, owner_silo_id, generation, expires_at, status)
            VALUES (@leaseToken, @accountId, @requestId, @siloId, @generation, @expiresAt, 'active')
            """, conn) { Transaction = tx })
        {
            insertCmd.Parameters.AddWithValue("leaseToken", leaseToken);
            insertCmd.Parameters.AddWithValue("accountId", accountId);
            insertCmd.Parameters.AddWithValue("requestId", requestId);
            insertCmd.Parameters.AddWithValue("siloId", siloId);
            insertCmd.Parameters.AddWithValue("generation", generation);
            insertCmd.Parameters.AddWithValue("expiresAt", expiresAt);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        // Increment active_count
        await using (var incrCmd = new NpgsqlCommand("""
            UPDATE account_concurrency_slots
            SET active_count = active_count + 1, updated_at = now()
            WHERE account_id = @accountId
            """, conn) { Transaction = tx })
        {
            incrCmd.Parameters.AddWithValue("accountId", accountId);
            await incrCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task ReleaseAccountSlot(string leaseToken, string siloId,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        long accountId;
        await using (var updateCmd = new NpgsqlCommand("""
            UPDATE account_slot_leases
            SET status = 'released', released_at = now()
            WHERE lease_token = @leaseToken AND status = 'active'
            RETURNING account_id
            """, conn) { Transaction = tx })
        {
            updateCmd.Parameters.AddWithValue("leaseToken", leaseToken);
            var result = await updateCmd.ExecuteScalarAsync(ct);
            if (result is null)
            {
                await tx.RollbackAsync(ct);
                return;
            }
            accountId = (long)result;
        }

        await using (var decrCmd = new NpgsqlCommand("""
            UPDATE account_concurrency_slots
            SET active_count = GREATEST(0, active_count - 1), updated_at = now()
            WHERE account_id = @accountId
            """, conn) { Transaction = tx })
        {
            decrCmd.Parameters.AddWithValue("accountId", accountId);
            await decrCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<int> ReclaimExpiredAccountSlots(long accountId, string siloId,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        int reclaimed;
        await using (var reclaimCmd = new NpgsqlCommand("""
            UPDATE account_slot_leases
            SET status = 'expired', released_at = now()
            WHERE account_id = @accountId AND status = 'active' AND expires_at <= now()
            """, conn) { Transaction = tx })
        {
            reclaimCmd.Parameters.AddWithValue("accountId", accountId);
            reclaimed = await reclaimCmd.ExecuteNonQueryAsync(ct);
        }

        if (reclaimed > 0)
        {
            await using var recountCmd = new NpgsqlCommand("""
                SELECT COUNT(*)::int FROM account_slot_leases
                WHERE account_id = @accountId AND status = 'active'
                """, conn) { Transaction = tx };
            recountCmd.Parameters.AddWithValue("accountId", accountId);
            var count = (int)(await recountCmd.ExecuteScalarAsync(ct))!;

            await using var updateCmd = new NpgsqlCommand("""
                UPDATE account_concurrency_slots
                SET active_count = @count, updated_at = now()
                WHERE account_id = @accountId
                """, conn) { Transaction = tx };
            updateCmd.Parameters.AddWithValue("accountId", accountId);
            updateCmd.Parameters.AddWithValue("count", count);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return reclaimed;
    }

    public async Task<int> GetAccountActiveCount(long accountId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT COALESCE(
                (SELECT active_count FROM account_concurrency_slots WHERE account_id = @accountId),
                0)
            """, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> TryAcquireUserSlot(long userId, string leaseToken,
        string requestId, string siloId, DateTime expiresAt, int maxConcurrency,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Ensure the slot row exists
        await using (var ensureCmd = new NpgsqlCommand("""
            INSERT INTO user_concurrency_slots (user_id, max_concurrency)
            VALUES (@userId, @maxConcurrency)
            ON CONFLICT (user_id) DO UPDATE
                SET max_concurrency = EXCLUDED.max_concurrency,
                    updated_at = now()
            """, conn) { Transaction = tx })
        {
            ensureCmd.Parameters.AddWithValue("userId", userId);
            ensureCmd.Parameters.AddWithValue("maxConcurrency", maxConcurrency);
            await ensureCmd.ExecuteNonQueryAsync(ct);
        }

        // Lock the row
        long generation;
        int activeCount;
        await using (var lockCmd = new NpgsqlCommand("""
            SELECT generation, active_count FROM user_concurrency_slots
            WHERE user_id = @userId FOR UPDATE
            """, conn) { Transaction = tx })
        {
            lockCmd.Parameters.AddWithValue("userId", userId);
            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false;
            generation = reader.GetInt64(0);
            activeCount = reader.GetInt32(1);
        }

        // Reclaim expired leases
        int reclaimed;
        await using (var reclaimCmd = new NpgsqlCommand("""
            UPDATE user_slot_leases
            SET status = 'expired', released_at = now()
            WHERE user_id = @userId AND status = 'active' AND expires_at <= now()
            """, conn) { Transaction = tx })
        {
            reclaimCmd.Parameters.AddWithValue("userId", userId);
            reclaimed = await reclaimCmd.ExecuteNonQueryAsync(ct);
        }

        if (reclaimed > 0)
        {
            await using var recountCmd = new NpgsqlCommand("""
                SELECT COUNT(*)::int FROM user_slot_leases
                WHERE user_id = @userId AND status = 'active'
                """, conn) { Transaction = tx };
            recountCmd.Parameters.AddWithValue("userId", userId);
            activeCount = (int)(await recountCmd.ExecuteScalarAsync(ct))!;

            await using var updateCmd = new NpgsqlCommand("""
                UPDATE user_concurrency_slots
                SET active_count = @count, updated_at = now()
                WHERE user_id = @userId
                """, conn) { Transaction = tx };
            updateCmd.Parameters.AddWithValue("userId", userId);
            updateCmd.Parameters.AddWithValue("count", activeCount);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        if (activeCount >= maxConcurrency)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // Insert lease
        await using (var insertCmd = new NpgsqlCommand("""
            INSERT INTO user_slot_leases
                (lease_token, user_id, request_id, owner_silo_id, generation, expires_at, status)
            VALUES (@leaseToken, @userId, @requestId, @siloId, @generation, @expiresAt, 'active')
            """, conn) { Transaction = tx })
        {
            insertCmd.Parameters.AddWithValue("leaseToken", leaseToken);
            insertCmd.Parameters.AddWithValue("userId", userId);
            insertCmd.Parameters.AddWithValue("requestId", requestId);
            insertCmd.Parameters.AddWithValue("siloId", siloId);
            insertCmd.Parameters.AddWithValue("generation", generation);
            insertCmd.Parameters.AddWithValue("expiresAt", expiresAt);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        // Increment active_count
        await using (var incrCmd = new NpgsqlCommand("""
            UPDATE user_concurrency_slots
            SET active_count = active_count + 1, updated_at = now()
            WHERE user_id = @userId
            """, conn) { Transaction = tx })
        {
            incrCmd.Parameters.AddWithValue("userId", userId);
            await incrCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task ReleaseUserSlot(string leaseToken, string siloId,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        long userId;
        await using (var updateCmd = new NpgsqlCommand("""
            UPDATE user_slot_leases
            SET status = 'released', released_at = now()
            WHERE lease_token = @leaseToken AND status = 'active'
            RETURNING user_id
            """, conn) { Transaction = tx })
        {
            updateCmd.Parameters.AddWithValue("leaseToken", leaseToken);
            var result = await updateCmd.ExecuteScalarAsync(ct);
            if (result is null)
            {
                await tx.RollbackAsync(ct);
                return;
            }
            userId = (long)result;
        }

        await using (var decrCmd = new NpgsqlCommand("""
            UPDATE user_concurrency_slots
            SET active_count = GREATEST(0, active_count - 1), updated_at = now()
            WHERE user_id = @userId
            """, conn) { Transaction = tx })
        {
            decrCmd.Parameters.AddWithValue("userId", userId);
            await decrCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<int> GetUserActiveCount(long userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT COALESCE(
                (SELECT active_count FROM user_concurrency_slots WHERE user_id = @userId),
                0)
            """, conn);
        cmd.Parameters.AddWithValue("userId", userId);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<AccountHealthState?> GetAccountHealthAsync(long accountId,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT account_id, consecutive_errors, last_success_at,
                   rate_limit_reset_at, overload_until, temp_unschedulable_until,
                   disabled_permanently, disable_reason
            FROM account_health_state
            WHERE account_id = @accountId
            """, conn);
        cmd.Parameters.AddWithValue("accountId", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new AccountHealthState
        {
            AccountId = reader.GetInt64(0),
            ConsecutiveErrors = reader.GetInt32(1),
            LastSuccessAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            RateLimitResetAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            OverloadUntil = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            TempUnschedulableUntil = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            DisabledPermanently = reader.GetBoolean(6),
            DisableReason = reader.IsDBNull(7) ? null : reader.GetString(7),
        };
    }

    public async Task UpdateAccountHealthAsync(long accountId, Action<AccountHealthState> mutate,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Ensure row exists
        await using (var ensureCmd = new NpgsqlCommand("""
            INSERT INTO account_health_state (account_id)
            VALUES (@accountId)
            ON CONFLICT (account_id) DO NOTHING
            """, conn) { Transaction = tx })
        {
            ensureCmd.Parameters.AddWithValue("accountId", accountId);
            await ensureCmd.ExecuteNonQueryAsync(ct);
        }

        // Read current state under lock
        AccountHealthState current;
        await using (var lockCmd = new NpgsqlCommand("""
            SELECT account_id, consecutive_errors, last_success_at,
                   rate_limit_reset_at, overload_until, temp_unschedulable_until,
                   disabled_permanently, disable_reason
            FROM account_health_state
            WHERE account_id = @accountId FOR UPDATE
            """, conn) { Transaction = tx })
        {
            lockCmd.Parameters.AddWithValue("accountId", accountId);
            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await tx.RollbackAsync(ct);
                return;
            }
            current = new AccountHealthState
            {
                AccountId = reader.GetInt64(0),
                ConsecutiveErrors = reader.GetInt32(1),
                LastSuccessAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                RateLimitResetAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                OverloadUntil = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                TempUnschedulableUntil = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                DisabledPermanently = reader.GetBoolean(6),
                DisableReason = reader.IsDBNull(7) ? null : reader.GetString(7),
            };
        }

        // Apply mutation
        var updated = current;
        mutate(updated);

        // Write back
        await using (var updateCmd = new NpgsqlCommand("""
            UPDATE account_health_state SET
                consecutive_errors = @consecutiveErrors,
                last_success_at = @lastSuccessAt,
                rate_limit_reset_at = @rateLimitResetAt,
                overload_until = @overloadUntil,
                temp_unschedulable_until = @tempUnschedulableUntil,
                disabled_permanently = @disabledPermanently,
                disable_reason = @disableReason,
                updated_at = now()
            WHERE account_id = @accountId
            """, conn) { Transaction = tx })
        {
            updateCmd.Parameters.AddWithValue("accountId", accountId);
            updateCmd.Parameters.AddWithValue("consecutiveErrors", updated.ConsecutiveErrors);
            updateCmd.Parameters.AddWithValue("lastSuccessAt", (object?)updated.LastSuccessAt ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("rateLimitResetAt", (object?)updated.RateLimitResetAt ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("overloadUntil", (object?)updated.OverloadUntil ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("tempUnschedulableUntil", (object?)updated.TempUnschedulableUntil ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("disabledPermanently", updated.DisabledPermanently);
            updateCmd.Parameters.AddWithValue("disableReason", (object?)updated.DisableReason ?? DBNull.Value);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
