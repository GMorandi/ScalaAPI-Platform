using Npgsql;

namespace ScalaAPI.Data.ProviderQuota;

/// <summary>
/// Snapshot of a provider account's quota state as persisted in
/// <c>provider_quota_state</c>. All timestamps are UTC.
/// </summary>
public sealed record ProviderQuotaSnapshot(
    long AccountId,
    string Tier,
    decimal? RemainingQuota,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    string? Source,
    DateTime FetchedAt,
    DateTime? ExpiresAt,
    long Generation,
    DateTime? CooldownUntil);

/// <summary>
/// Outcome of a <see cref="IProviderQuotaStore.TryReserveAsync"/> call.
/// </summary>
public enum QuotaReservationStatus
{
    Reserved,
    InsufficientQuota,
    Expired,
    Cooldown,
    UnknownTier,
    NoSnapshot,
}

/// <summary>
/// A successful reservation that can later be settled.
/// </summary>
public sealed record QuotaReservation(
    QuotaReservationStatus Status,
    string? LeaseId,
    long AccountId,
    decimal EstimatedCost,
    decimal? RemainingAfter);

/// <summary>
/// How a request completed so the reservation can be settled.
/// </summary>
public enum QuotaSettlementOutcome
{
    Success,
    Rejected,
    Unknown,
}

/// <summary>
/// Result of a <see cref="IProviderQuotaStore.SettleAsync"/> call.
/// </summary>
public sealed record QuotaSettlementResult(
    bool Applied,
    decimal? ActualCost,
    decimal? RemainingAfter);

/// <summary>
/// Result of a CAS-based refresh.
/// </summary>
public sealed record QuotaRefreshResult(
    bool Applied,
    long Generation,
    string? LockToken);

/// <summary>
/// Persists provider tier/quota snapshots with CAS-based refresh,
/// atomic reservation, and settlement. Uses advisory locks for
/// concurrency safety across silos.
/// </summary>
public interface IProviderQuotaStore
{
    /// <summary>Returns the current snapshot or null if no row exists.</summary>
    Task<ProviderQuotaSnapshot?> GetAsync(long accountId, CancellationToken ct = default);

    /// <summary>
    /// CAS-based refresh: acquires an advisory lock, reads the current generation,
    /// calls the updater to produce new values, and writes only if the generation
    /// has not changed. Two concurrent refreshes produce only one valid generation.
    /// </summary>
    Task<QuotaRefreshResult> RefreshAsync(
        long accountId,
        Func<ProviderQuotaSnapshot?, ProviderQuotaUpdate> updater,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically reserves estimated cost against the remaining quota.
    /// Returns <see cref="QuotaReservationStatus.Reserved"/> on success.
    /// </summary>
    Task<QuotaReservation> TryReserveAsync(
        long accountId, decimal estimatedCost, CancellationToken ct = default);

    /// <summary>
    /// Settles a previously reserved cost. On <see cref="QuotaSettlementOutcome.Success"/>,
    /// the actual cost replaces the estimate. On <see cref="QuotaSettlementOutcome.Rejected"/>,
    /// the full estimate is returned. On <see cref="QuotaSettlementOutcome.Unknown"/>,
    /// the estimate is held (no change) for later reconciliation.
    /// </summary>
    Task<QuotaSettlementResult> SettleAsync(
        long accountId, string leaseId, decimal actualCost,
        QuotaSettlementOutcome outcome, CancellationToken ct = default);

    /// <summary>
    /// Records a backoff/cooldown period for the account, typically after a 429.
    /// </summary>
    Task RecordBackoffAsync(
        long accountId, TimeSpan backoff, CancellationToken ct = default);
}

/// <summary>
/// Values supplied by the updater callback during
/// <see cref="IProviderQuotaStore.RefreshAsync"/>.
/// </summary>
public sealed record ProviderQuotaUpdate(
    string Tier,
    decimal? RemainingQuota,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    string? Source,
    DateTime? ExpiresAt);

public sealed class ProviderQuotaStore(NpgsqlDataSource dataSource) : IProviderQuotaStore
{
    /// <inheritdoc />
    public async Task<ProviderQuotaSnapshot?> GetAsync(
        long accountId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand();
        command.CommandText = """
            SELECT account_id, tier, remaining_quota, window_start, window_end,
                   source, fetched_at, expires_at, generation, cooldown_until
            FROM provider_quota_state
            WHERE account_id = $1
            """;
        command.Parameters.AddWithValue(accountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadSnapshot(reader);
    }

    /// <inheritdoc />
    public async Task<QuotaRefreshResult> RefreshAsync(
        long accountId,
        Func<ProviderQuotaSnapshot?, ProviderQuotaUpdate> updater,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Advisory lock keyed on account_id ensures only one silo refreshes at a time.
        await AcquireAdvisoryLock(connection, transaction, accountId, ct);

        var current = await ReadSnapshotInTransaction(connection, transaction, accountId, ct);

        var update = updater(current);
        if (update is null)
        {
            await transaction.CommitAsync(ct);
            return new QuotaRefreshResult(false, current?.Generation ?? 0, null);
        }

        var newGeneration = (current?.Generation ?? 0) + 1;
        var lockToken = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO provider_quota_state (
                account_id, tier, remaining_quota, window_start, window_end,
                source, fetched_at, expires_at, generation,
                refresh_lock_until, refresh_lock_token, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (account_id) DO UPDATE
            SET tier = EXCLUDED.tier,
                remaining_quota = EXCLUDED.remaining_quota,
                window_start = EXCLUDED.window_start,
                window_end = EXCLUDED.window_end,
                source = EXCLUDED.source,
                fetched_at = EXCLUDED.fetched_at,
                expires_at = EXCLUDED.expires_at,
                generation = EXCLUDED.generation,
                refresh_lock_until = EXCLUDED.refresh_lock_until,
                refresh_lock_token = EXCLUDED.refresh_lock_token,
                updated_at = EXCLUDED.updated_at
            WHERE provider_quota_state.generation < EXCLUDED.generation
               OR (provider_quota_state.generation = EXCLUDED.generation - 1
                   AND provider_quota_state.account_id = EXCLUDED.account_id)
            """;
        upsert.Parameters.AddWithValue(accountId);
        upsert.Parameters.AddWithValue(update.Tier);
        upsert.Parameters.AddWithValue((object?)update.RemainingQuota ?? DBNull.Value);
        upsert.Parameters.AddWithValue((object?)update.WindowStart ?? DBNull.Value);
        upsert.Parameters.AddWithValue((object?)update.WindowEnd ?? DBNull.Value);
        upsert.Parameters.AddWithValue((object?)update.Source ?? DBNull.Value);
        upsert.Parameters.AddWithValue(now);
        upsert.Parameters.AddWithValue((object?)update.ExpiresAt ?? DBNull.Value);
        upsert.Parameters.AddWithValue(newGeneration);
        upsert.Parameters.AddWithValue(DBNull.Value); // lock cleared after refresh
        upsert.Parameters.AddWithValue(DBNull.Value);
        upsert.Parameters.AddWithValue(now);
        await upsert.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return new QuotaRefreshResult(true, newGeneration, lockToken);
    }

    /// <inheritdoc />
    public async Task<QuotaReservation> TryReserveAsync(
        long accountId, decimal estimatedCost, CancellationToken ct = default)
    {
        if (estimatedCost < 0m)
            throw new ArgumentOutOfRangeException(nameof(estimatedCost));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await AcquireAdvisoryLock(connection, transaction, accountId, ct);

        var current = await ReadSnapshotInTransaction(connection, transaction, accountId, ct);
        if (current is null)
        {
            await transaction.CommitAsync(ct);
            return new QuotaReservation(QuotaReservationStatus.NoSnapshot, null, accountId, estimatedCost, null);
        }

        // Check cooldown
        if (current.CooldownUntil.HasValue && current.CooldownUntil.Value > DateTime.UtcNow)
        {
            await transaction.CommitAsync(ct);
            return new QuotaReservation(QuotaReservationStatus.Cooldown, null, accountId, estimatedCost, null);
        }

        // Check expiry: expired snapshots don't allow expensive requests
        if (current.ExpiresAt.HasValue && current.ExpiresAt.Value <= DateTime.UtcNow)
        {
            await transaction.CommitAsync(ct);
            return new QuotaReservation(QuotaReservationStatus.Expired, null, accountId, estimatedCost, null);
        }

        // Unknown/free-tier: if tier is "unknown" and no quota info, allow with no deduction
        if (string.Equals(current.Tier, "unknown", StringComparison.OrdinalIgnoreCase)
            && !current.RemainingQuota.HasValue)
        {
            await transaction.CommitAsync(ct);
            return new QuotaReservation(QuotaReservationStatus.UnknownTier, null, accountId, estimatedCost, null);
        }

        // Free tier: no quota tracking needed
        if (string.Equals(current.Tier, "free", StringComparison.OrdinalIgnoreCase))
        {
            var leaseId = Guid.NewGuid().ToString("N");
            await transaction.CommitAsync(ct);
            return new QuotaReservation(QuotaReservationStatus.Reserved, leaseId, accountId, estimatedCost, current.RemainingQuota);
        }

        // Check remaining quota
        if (!current.RemainingQuota.HasValue || current.RemainingQuota.Value < estimatedCost)
        {
            await transaction.CommitAsync(ct);
            return new QuotaReservation(QuotaReservationStatus.InsufficientQuota, null, accountId, estimatedCost, current.RemainingQuota);
        }

        // Atomic reservation: deduct estimated cost
        var remainingAfter = current.RemainingQuota.Value - estimatedCost;
        var reservationLeaseId = Guid.NewGuid().ToString("N");

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE provider_quota_state
            SET remaining_quota = remaining_quota - $2,
                updated_at = now()
            WHERE account_id = $1
              AND remaining_quota >= $2
            RETURNING remaining_quota
            """;
        update.Parameters.AddWithValue(accountId);
        update.Parameters.AddWithValue(estimatedCost);
        var result = await update.ExecuteScalarAsync(ct);
        if (result is null)
        {
            await transaction.CommitAsync(ct);
            return new QuotaReservation(QuotaReservationStatus.InsufficientQuota, null, accountId, estimatedCost, current.RemainingQuota);
        }

        // Record the reservation in a hold-style tracking row for settlement
        await using var insertReservation = connection.CreateCommand();
        insertReservation.Transaction = transaction;
        insertReservation.CommandText = """
            INSERT INTO provider_quota_reservations (
                lease_id, account_id, estimated_cost, status, created_at)
            VALUES ($1, $2, $3, 'reserved', now())
            ON CONFLICT (lease_id) DO NOTHING
            """;
        insertReservation.Parameters.AddWithValue(reservationLeaseId);
        insertReservation.Parameters.AddWithValue(accountId);
        insertReservation.Parameters.AddWithValue(estimatedCost);
        await insertReservation.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return new QuotaReservation(QuotaReservationStatus.Reserved, reservationLeaseId, accountId, estimatedCost, remainingAfter);
    }

    /// <inheritdoc />
    public async Task<QuotaSettlementResult> SettleAsync(
        long accountId, string leaseId, decimal actualCost,
        QuotaSettlementOutcome outcome, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return new QuotaSettlementResult(false, null, null);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await AcquireAdvisoryLock(connection, transaction, accountId, ct);

        // Read the reservation
        await using var readReservation = connection.CreateCommand();
        readReservation.Transaction = transaction;
        readReservation.CommandText = """
            SELECT estimated_cost, status FROM provider_quota_reservations
            WHERE lease_id = $1 AND account_id = $2
            FOR UPDATE
            """;
        readReservation.Parameters.AddWithValue(leaseId);
        readReservation.Parameters.AddWithValue(accountId);
        await using var reader = await readReservation.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await transaction.CommitAsync(ct);
            return new QuotaSettlementResult(false, null, null);
        }
        var estimatedCost = reader.GetDecimal(0);
        var status = reader.GetString(1);
        await reader.DisposeAsync();

        if (status != "reserved")
        {
            await transaction.CommitAsync(ct);
            return new QuotaSettlementResult(false, null, null);
        }

        decimal? adjustment = null;
        switch (outcome)
        {
            case QuotaSettlementOutcome.Success:
                // Return the difference: estimated was deducted, now correct to actual
                adjustment = estimatedCost - actualCost;
                break;
            case QuotaSettlementOutcome.Rejected:
                // Return the full estimated cost
                adjustment = estimatedCost;
                break;
            case QuotaSettlementOutcome.Unknown:
                // Hold the estimate; no adjustment (reconciliation will handle later)
                adjustment = 0m;
                break;
        }

        if (adjustment.HasValue && adjustment.Value != 0m)
        {
            await using var adjustQuota = connection.CreateCommand();
            adjustQuota.Transaction = transaction;
            adjustQuota.CommandText = """
                UPDATE provider_quota_state
                SET remaining_quota = remaining_quota + $2,
                    updated_at = now()
                WHERE account_id = $1
                """;
            adjustQuota.Parameters.AddWithValue(accountId);
            adjustQuota.Parameters.AddWithValue(adjustment.Value);
            await adjustQuota.ExecuteNonQueryAsync(ct);
        }

        // Mark reservation as settled
        var settlementStatus = outcome switch
        {
            QuotaSettlementOutcome.Success => "settled",
            QuotaSettlementOutcome.Rejected => "released",
            QuotaSettlementOutcome.Unknown => "held",
            _ => "settled"
        };

        await using var markSettled = connection.CreateCommand();
        markSettled.Transaction = transaction;
        markSettled.CommandText = """
            UPDATE provider_quota_reservations
            SET status = $3, actual_cost = $4, settled_at = now()
            WHERE lease_id = $1 AND account_id = $2
            """;
        markSettled.Parameters.AddWithValue(leaseId);
        markSettled.Parameters.AddWithValue(accountId);
        markSettled.Parameters.AddWithValue(settlementStatus);
        markSettled.Parameters.AddWithValue((object?)actualCost ?? DBNull.Value);
        await markSettled.ExecuteNonQueryAsync(ct);

        // Read back remaining for the result
        decimal? remainingAfter = null;
        await using var readRemaining = connection.CreateCommand();
        readRemaining.Transaction = transaction;
        readRemaining.CommandText = """
            SELECT remaining_quota FROM provider_quota_state
            WHERE account_id = $1
            """;
        readRemaining.Parameters.AddWithValue(accountId);
        var remainingValue = await readRemaining.ExecuteScalarAsync(ct);
        if (remainingValue is not null)
            remainingAfter = Convert.ToDecimal(remainingValue);

        await transaction.CommitAsync(ct);
        return new QuotaSettlementResult(true, actualCost, remainingAfter);
    }

    /// <inheritdoc />
    public async Task RecordBackoffAsync(
        long accountId, TimeSpan backoff, CancellationToken ct = default)
    {
        var cooldownUntil = DateTime.UtcNow.Add(backoff);
        await using var command = dataSource.CreateCommand();
        command.CommandText = """
            UPDATE provider_quota_state
            SET cooldown_until = $2,
                updated_at = now()
            WHERE account_id = $1
            """;
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(cooldownUntil);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Ensures the reservation tracking table exists. Called during tests.
    /// </summary>
    public async Task EnsureReservationTableAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_quota_reservations (
                lease_id text PRIMARY KEY,
                account_id bigint NOT NULL,
                estimated_cost numeric(20,8) NOT NULL,
                actual_cost numeric(20,8),
                status text NOT NULL DEFAULT 'reserved',
                created_at timestamptz NOT NULL DEFAULT now(),
                settled_at timestamptz
            );
            CREATE INDEX IF NOT EXISTS idx_pqr_account ON provider_quota_reservations(account_id);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AcquireAdvisoryLock(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long accountId, CancellationToken ct)
    {
        // Use a namespace offset to avoid collisions with other advisory locks
        var lockKey = accountId + 8_700_000_000L;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock($1)";
        command.Parameters.AddWithValue(lockKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ProviderQuotaSnapshot?> ReadSnapshotInTransaction(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long accountId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account_id, tier, remaining_quota, window_start, window_end,
                   source, fetched_at, expires_at, generation, cooldown_until
            FROM provider_quota_state
            WHERE account_id = $1
            """;
        command.Parameters.AddWithValue(accountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadSnapshot(reader);
    }

    private static ProviderQuotaSnapshot ReadSnapshot(NpgsqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetDateTime(9));
}
