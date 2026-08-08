using System.Data;
using Npgsql;

namespace ScalaAPI.Data.Accounting;

public enum AccountingEffectStatus
{
    Created,
    Replay,
    Conflict,
    InsufficientFunds,
}

public sealed record AccountingSnapshot(long UserId, long Version, decimal Balance);

public sealed record AccountingEffect(
    long UserId,
    string EffectId,
    string EntryType,
    decimal Amount,
    long? PaymentId = null,
    string? LeaseToken = null,
    string? IdempotencyKey = null,
    string Description = "",
    long? CreatedBy = null,
    decimal? MinimumBalance = null);

public sealed record AccountingEffectResult(
    AccountingEffectStatus Status,
    string EffectId,
    long? LedgerId,
    AccountingSnapshot Snapshot);

public sealed record AccountingProjection(
    long UserId,
    long Version,
    decimal Balance,
    int Attempts);

public sealed class AccountingStore(NpgsqlDataSource dataSource)
{
    public async Task EnsureAccountAsync(long userId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO accounting_accounts(user_id) VALUES ($1)
            ON CONFLICT (user_id) DO NOTHING
            """);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AccountingEffectResult> AppendEffectAsync(
        AccountingEffect effect,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        var result = await AppendEffectAsync(connection, transaction, effect, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<AccountingEffectResult> AppendEffectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountingEffect effect,
        CancellationToken ct = default)
    {
        ValidateEffect(effect);
        await AcquireUserLockAsync(connection, transaction, effect.UserId, ct);
        await EnsureAccountAsync(connection, transaction, effect.UserId, ct);

        var existing = await FindEffectAsync(
            connection, transaction, effect.UserId, effect.EffectId, effect.EntryType, ct);
        if (existing is not null)
        {
            var snapshot = await GetSnapshotAsync(connection, transaction, effect.UserId, ct);
            var status = Matches(existing, effect)
                ? AccountingEffectStatus.Replay
                : AccountingEffectStatus.Conflict;
            return new(status, effect.EffectId, existing.Id, snapshot);
        }

        var current = await GetSnapshotAsync(connection, transaction, effect.UserId, ct);
        var balanceAfter = current.Balance + effect.Amount;
        if (effect.MinimumBalance.HasValue && balanceAfter < effect.MinimumBalance.Value)
            return new(AccountingEffectStatus.InsufficientFunds, effect.EffectId, null, current);

        AccountingSnapshot next;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE accounting_accounts
                SET posted_balance = posted_balance + $2,
                    ledger_version = ledger_version + 1,
                    updated_at = now()
                WHERE user_id = $1
                RETURNING ledger_version, posted_balance
                """;
            update.Parameters.AddWithValue(effect.UserId);
            update.Parameters.AddWithValue(effect.Amount);
            await using var reader = await update.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Accounting account {effect.UserId} disappeared");
            next = new(effect.UserId, reader.GetInt64(0), reader.GetDecimal(1));
        }

        long ledgerId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO balance_ledger(
                    user_id, payment_id, reference, amount, lease_token, entry_type,
                    idempotency_key, description, created_by, ledger_version)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                RETURNING id
                """;
            insert.Parameters.AddWithValue(effect.UserId);
            insert.Parameters.AddWithValue((object?)effect.PaymentId ?? DBNull.Value);
            insert.Parameters.AddWithValue(effect.EffectId);
            insert.Parameters.AddWithValue(effect.Amount);
            insert.Parameters.AddWithValue((object?)effect.LeaseToken ?? DBNull.Value);
            insert.Parameters.AddWithValue(effect.EntryType);
            insert.Parameters.AddWithValue((object?)effect.IdempotencyKey ?? DBNull.Value);
            insert.Parameters.AddWithValue(effect.Description);
            insert.Parameters.AddWithValue((object?)effect.CreatedBy ?? DBNull.Value);
            insert.Parameters.AddWithValue(next.Version);
            ledgerId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }

        await QueueProjectionAsync(connection, transaction, next, ct);
        return new(AccountingEffectStatus.Created, effect.EffectId, ledgerId, next);
    }

    public async Task<bool> TryReserveHoldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        string holdId,
        string leaseToken,
        decimal amount,
        CancellationToken ct = default)
    {
        if (amount < 0m || decimal.Round(amount, 8) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount == 0m) return true;
        if (string.IsNullOrWhiteSpace(holdId))
            throw new ArgumentException("Hold ID is required", nameof(holdId));

        await AcquireUserLockAsync(connection, transaction, userId, ct);
        await EnsureAccountAsync(connection, transaction, userId, ct);

        decimal available;
        await using (var balance = connection.CreateCommand())
        {
            balance.Transaction = transaction;
            balance.CommandText = """
                SELECT account.posted_balance - COALESCE((
                    SELECT sum(amount) FROM balance_holds
                    WHERE user_id = $1 AND status = 'active'
                ), 0)
                FROM accounting_accounts account
                WHERE account.user_id = $1
                """;
            balance.Parameters.AddWithValue(userId);
            available = Convert.ToDecimal(await balance.ExecuteScalarAsync(ct));
        }
        if (available < amount) return false;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO balance_holds(hold_id, user_id, lease_token, amount, status)
            VALUES ($1, $2, $3, $4, 'active')
            ON CONFLICT (hold_id) DO NOTHING
            RETURNING hold_id
            """;
        insert.Parameters.AddWithValue(holdId);
        insert.Parameters.AddWithValue(userId);
        insert.Parameters.AddWithValue(leaseToken);
        insert.Parameters.AddWithValue(amount);
        return await insert.ExecuteScalarAsync(ct) is not null;
    }

    public async Task FinalizeHoldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        string? holdId,
        string status,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(holdId)) return;
        if (status is not ("released" or "committed"))
            throw new ArgumentOutOfRangeException(nameof(status));

        await AcquireUserLockAsync(connection, transaction, userId, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE balance_holds
            SET status = $2, finalized_at = now()
            WHERE hold_id = $1 AND user_id = $3 AND status = 'active'
            """;
        command.Parameters.AddWithValue(holdId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AccountingSnapshot> GetSnapshotAsync(
        long userId,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureAccountAsync(connection, transaction, userId, ct);
        var snapshot = await GetSnapshotAsync(connection, transaction, userId, ct);
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<IReadOnlyList<AccountingProjection>> ClaimProjectionBatchAsync(
        string workerId,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
                SELECT user_id
                FROM accounting_projection_outbox
                WHERE next_attempt_at <= now()
                  AND (claimed_until IS NULL OR claimed_until <= now())
                ORDER BY next_attempt_at, user_id
                FOR UPDATE SKIP LOCKED
                LIMIT $1
            )
            UPDATE accounting_projection_outbox outbox
            SET claimed_by = $2,
                claimed_until = now() + interval '30 seconds',
                attempts = outbox.attempts + 1,
                updated_at = now()
            FROM candidates
            WHERE outbox.user_id = candidates.user_id
            RETURNING outbox.user_id, outbox.ledger_version,
                      outbox.posted_balance, outbox.attempts
            """;
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 1000));
        command.Parameters.AddWithValue(workerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<AccountingProjection>();
        while (await reader.ReadAsync(ct))
            items.Add(new(reader.GetInt64(0), reader.GetInt64(1),
                reader.GetDecimal(2), reader.GetInt32(3)));
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return items;
    }

    public async Task MarkProjectionAppliedAsync(
        long userId,
        long version,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM accounting_projection_outbox
            WHERE user_id = $1 AND ledger_version <= $2
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(version);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkProjectionFailedAsync(
        AccountingProjection projection,
        Exception error,
        CancellationToken ct = default)
    {
        var message = error.Message.Length > 500 ? error.Message[..500] : error.Message;
        await using var command = dataSource.CreateCommand("""
            UPDATE accounting_projection_outbox
            SET claimed_by = NULL,
                claimed_until = NULL,
                next_attempt_at = now() + LEAST(
                    interval '5 minutes',
                    interval '1 second' * power(2, LEAST(attempts, 8))),
                last_error = $3,
                updated_at = now()
            WHERE user_id = $1 AND ledger_version = $2
            """);
        command.Parameters.AddWithValue(projection.UserId);
        command.Parameters.AddWithValue(projection.Version);
        command.Parameters.AddWithValue(message);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task AcquireUserLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock($1)";
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateEffect(AccountingEffect effect)
    {
        if (effect.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(effect.UserId));
        if (string.IsNullOrWhiteSpace(effect.EffectId) || effect.EffectId.Length > 500)
            throw new ArgumentException("Effect ID must contain 1-500 characters", nameof(effect));
        if (string.IsNullOrWhiteSpace(effect.EntryType) || effect.EntryType.Length > 100)
            throw new ArgumentException("Entry type must contain 1-100 characters", nameof(effect));
        if (effect.Amount == 0m || decimal.Round(effect.Amount, 8) != effect.Amount)
            throw new ArgumentOutOfRangeException(nameof(effect.Amount));
    }

    private static async Task EnsureAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO accounting_accounts(user_id) VALUES ($1)
            ON CONFLICT (user_id) DO NOTHING
            """;
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<AccountingSnapshot> GetSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ledger_version, posted_balance
            FROM accounting_accounts WHERE user_id = $1
            """;
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Accounting account {userId} was not initialized");
        return new(userId, reader.GetInt64(0), reader.GetDecimal(1));
    }

    private static async Task<ExistingEffect?> FindEffectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long userId,
        string effectId,
        string entryType,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, amount, payment_id, lease_token, idempotency_key,
                   description
            FROM balance_ledger
            WHERE user_id = $1 AND reference = $2 AND entry_type = $3
            FOR UPDATE
            """;
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(effectId);
        command.Parameters.AddWithValue(entryType);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(
            reader.GetInt64(0),
            reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5));
    }

    private static bool Matches(ExistingEffect existing, AccountingEffect effect) =>
        existing.Amount == effect.Amount
        && existing.PaymentId == effect.PaymentId
        && string.Equals(existing.LeaseToken, effect.LeaseToken, StringComparison.Ordinal)
        && string.Equals(existing.IdempotencyKey, effect.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(existing.Description, effect.Description, StringComparison.Ordinal);

    private static async Task QueueProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountingSnapshot snapshot,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO accounting_projection_outbox(
                user_id, ledger_version, posted_balance)
            VALUES ($1, $2, $3)
            ON CONFLICT (user_id) DO UPDATE
            SET ledger_version = EXCLUDED.ledger_version,
                posted_balance = EXCLUDED.posted_balance,
                attempts = 0,
                next_attempt_at = now(),
                claimed_by = NULL,
                claimed_until = NULL,
                last_error = NULL,
                updated_at = now()
            WHERE accounting_projection_outbox.ledger_version < EXCLUDED.ledger_version
            """;
        command.Parameters.AddWithValue(snapshot.UserId);
        command.Parameters.AddWithValue(snapshot.Version);
        command.Parameters.AddWithValue(snapshot.Balance);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record ExistingEffect(
        long Id,
        decimal Amount,
        long? PaymentId,
        string? LeaseToken,
        string? IdempotencyKey,
        string Description);
}
