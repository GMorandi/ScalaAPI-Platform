using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace ScalaAPI.Data.Accounting;

public enum ProjectionRepairState
{
    Consistent,
    Repaired,
    Failed,
}

public sealed record ProjectionRepairResult(
    ProjectionRepairState State,
    long ActualVersion,
    decimal ActualBalance,
    string Error = "");

public interface IAccountingProjectionRepairer
{
    Task<ProjectionRepairResult> RepairAsync(
        AccountingSnapshot expected,
        CancellationToken ct = default);
}

public sealed record AccountingReconciliationResult(
    bool Started,
    long? RunId,
    string Status,
    long CheckedAccounts,
    long RepairedHolds,
    long RepairedProjections,
    long OpenIncidents,
    long ResolvedIncidents,
    decimal LedgerTotal,
    decimal HoldTotal,
    decimal MismatchTotal)
{
    public static AccountingReconciliationResult Busy() =>
        new(false, null, "busy", 0, 0, 0, 0, 0, 0m, 0m, 0m);
}

public sealed class AccountingReconciliationService(
    NpgsqlDataSource dataSource,
    IAccountingProjectionRepairer projectionRepairer,
    ILogger<AccountingReconciliationService> logger)
{
    public async Task<AccountingReconciliationResult> RunAsync(
        string trigger,
        CancellationToken ct = default)
    {
        trigger = string.IsNullOrWhiteSpace(trigger) ? "unspecified" : trigger.Trim();
        if (trigger.Length > 100) trigger = trigger[..100];

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        if (!await TryAcquireRunLockAsync(connection, ct))
            return AccountingReconciliationResult.Busy();

        long? runId = null;
        try
        {
            await MarkAbandonedRunsAsync(connection, ct);
            runId = await StartRunAsync(connection, trigger, ct);
            var accounting = await ScanAccountingAsync(
                connection, runId.Value, ct);
            var repairedHolds = await RepairTerminalHoldsAsync(connection, ct);
            var usageMismatch = await ScanUsageDebitsAsync(
                connection, runId.Value, ct);
            await ScanLeaseHoldsAsync(connection, runId.Value, ct);

            var repairedProjections = await ReconcileProjectionsAsync(
                connection, runId.Value, accounting.Snapshots,
                accounting.InvalidUsers, ct);
            var resolved = await ResolveUnseenIncidentsAsync(
                connection, runId.Value, ct);
            var open = await CountOpenIncidentsAsync(connection, ct);
            var holdTotal = await ReadDecimalAsync(connection,
                "SELECT COALESCE(sum(amount), 0) FROM balance_holds WHERE status = 'active'", ct);
            var ledgerTotal = await ReadDecimalAsync(connection,
                "SELECT COALESCE(sum(-amount), 0) FROM balance_ledger WHERE entry_type = 'usage_debit'", ct);
            var mismatchTotal = decimal.Round(
                accounting.MismatchTotal + usageMismatch, 8, MidpointRounding.AwayFromZero);
            var status = open == 0 ? "passed" : "failed";

            await CompleteRunAsync(connection, runId.Value, status,
                accounting.Snapshots.Count, repairedHolds, repairedProjections,
                open, resolved, ledgerTotal, holdTotal, mismatchTotal, trigger, ct);

            return new(true, runId, status, accounting.Snapshots.Count,
                repairedHolds, repairedProjections, open, resolved,
                ledgerTotal, holdTotal, mismatchTotal);
        }
        catch (Exception ex)
        {
            if (runId.HasValue)
            {
                try
                {
                    await FailRunAsync(connection, runId.Value, ex, CancellationToken.None);
                }
                catch (Exception failureError)
                {
                    logger.LogError(failureError,
                        "Failed to persist reconciliation run {RunId} failure", runId);
                }
            }
            throw;
        }
        finally
        {
            try
            {
                await ReleaseRunLockAsync(connection);
            }
            catch (Exception unlockError)
            {
                logger.LogError(unlockError,
                    "Failed to release accounting reconciliation advisory lock");
            }
        }
    }

    private static async Task<bool> TryAcquireRunLockAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_try_advisory_lock(hashtext('scalaapi'), hashtext('accounting-reconciliation'))";
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task ReleaseRunLockAsync(NpgsqlConnection connection)
    {
        if (connection.FullState != ConnectionState.Open) return;
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_advisory_unlock(hashtext('scalaapi'), hashtext('accounting-reconciliation'))";
        await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task MarkAbandonedRunsAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ledger_reconciliation_runs
            SET status = 'failed',
                completed_at = now(),
                details = details || jsonb_build_object(
                    'failure', 'worker_lost_before_completion',
                    'recovered_at', now())
            WHERE status = 'running'
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> StartRunAsync(
        NpgsqlConnection connection,
        string trigger,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ledger_reconciliation_runs(status, details)
            VALUES ('running', jsonb_build_object('trigger', $1))
            RETURNING id
            """;
        command.Parameters.AddWithValue(trigger);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private async Task<AccountingScan> ScanAccountingAsync(
        NpgsqlConnection connection,
        long runId,
        CancellationToken ct)
    {
        var invalidUsers = new HashSet<long>();
        var mismatchTotal = 0m;
        var anomalies = new List<ReconciliationAnomaly>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH ledger AS (
                    SELECT user_id,
                           COALESCE(sum(amount), 0) AS balance,
                           COALESCE(max(ledger_version), 0) AS max_version,
                           count(*) AS entry_count,
                           count(DISTINCT ledger_version) AS distinct_versions,
                           COALESCE(min(ledger_version), 0) AS min_version
                    FROM balance_ledger
                    GROUP BY user_id
                )
                SELECT COALESCE(account.user_id, ledger.user_id) AS user_id,
                       account.user_id IS NOT NULL AS account_exists,
                       COALESCE(account.posted_balance, 0),
                       COALESCE(account.ledger_version, 0),
                       ledger.user_id IS NOT NULL AS ledger_exists,
                       COALESCE(ledger.balance, 0),
                       COALESCE(ledger.max_version, 0),
                       COALESCE(ledger.entry_count, 0),
                       COALESCE(ledger.distinct_versions, 0),
                       COALESCE(ledger.min_version, 0)
                FROM accounting_accounts account
                FULL OUTER JOIN ledger ON ledger.user_id = account.user_id
                WHERE account.user_id IS NULL
                   OR account.posted_balance <> COALESCE(ledger.balance, 0)
                   OR account.ledger_version <> COALESCE(ledger.max_version, 0)
                   OR COALESCE(ledger.entry_count, 0) <> COALESCE(ledger.distinct_versions, 0)
                   OR (COALESCE(ledger.entry_count, 0) > 0
                       AND (ledger.min_version <> 1
                            OR ledger.max_version <> ledger.entry_count))
                ORDER BY COALESCE(account.user_id, ledger.user_id)
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var userId = reader.GetInt64(0);
                var accountExists = reader.GetBoolean(1);
                var accountBalance = reader.GetDecimal(2);
                var accountVersion = reader.GetInt64(3);
                var ledgerExists = reader.GetBoolean(4);
                var ledgerBalance = reader.GetDecimal(5);
                var ledgerVersion = reader.GetInt64(6);
                var entryCount = reader.GetInt64(7);
                var distinctVersions = reader.GetInt64(8);
                var minimumVersion = reader.GetInt64(9);
                invalidUsers.Add(userId);
                mismatchTotal += Math.Abs(accountBalance - ledgerBalance);

                anomalies.Add(new ReconciliationAnomaly(
                        $"account-ledger:{userId}", "account_ledger_mismatch", "critical",
                        userId, null,
                        new
                        {
                            account_exists = true,
                            balance_equals_ledger = true,
                            account_version_equals_ledger_max = true,
                            versions_start_at_one_and_are_contiguous = true,
                        },
                        new
                        {
                            account_exists = accountExists,
                            account_balance = accountBalance,
                            account_version = accountVersion,
                            ledger_exists = ledgerExists,
                            ledger_balance = ledgerBalance,
                            ledger_max_version = ledgerVersion,
                            ledger_entries = entryCount,
                            distinct_versions = distinctVersions,
                            minimum_version = minimumVersion,
                        }));
            }
        }

        foreach (var anomaly in anomalies)
            await UpsertIncidentAsync(connection, runId, anomaly, ct);

        var snapshots = new List<AccountingSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT user_id, ledger_version, posted_balance
                FROM accounting_accounts
                ORDER BY user_id
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                snapshots.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetDecimal(2)));
        }
        return new(snapshots, invalidUsers, mismatchTotal);
    }

    private static async Task<long> RepairTerminalHoldsAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        var candidates = new List<(string HoldId, long UserId, string ExpectedStatus)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT hold.hold_id, hold.user_id,
                       CASE WHEN lease.status = 'completed' THEN 'committed'
                            ELSE 'released' END
                FROM balance_holds hold
                JOIN request_leases lease ON lease.lease_token = hold.lease_token
                WHERE hold.status = 'active'
                  AND lease.status IN ('completed', 'aborted', 'expired')
                ORDER BY hold.user_id, hold.hold_id
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                candidates.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        long repaired = 0;
        foreach (var candidate in candidates)
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await AccountingStore.AcquireUserLockAsync(
                connection, transaction, candidate.UserId, ct);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE balance_holds hold
                SET status = $2, finalized_at = now()
                FROM request_leases lease
                WHERE hold.hold_id = $1
                  AND hold.lease_token = lease.lease_token
                  AND hold.status = 'active'
                  AND (($2 = 'committed' AND lease.status = 'completed')
                    OR ($2 = 'released' AND lease.status IN ('aborted', 'expired')))
                """;
            update.Parameters.AddWithValue(candidate.HoldId);
            update.Parameters.AddWithValue(candidate.ExpectedStatus);
            repaired += await update.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return repaired;
    }

    private async Task<decimal> ScanUsageDebitsAsync(
        NpgsqlConnection connection,
        long runId,
        CancellationToken ct)
    {
        var mismatchTotal = 0m;
        var anomalies = new List<ReconciliationAnomaly>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH debits AS (
                SELECT lease_token, COALESCE(sum(-amount), 0) AS debit
                FROM balance_ledger
                WHERE entry_type = 'usage_debit' AND lease_token IS NOT NULL
                GROUP BY lease_token
            )
            SELECT COALESCE(usage.lease_token, debits.lease_token),
                   usage.lease_token IS NOT NULL,
                   COALESCE(usage.user_id, lease.user_id),
                   COALESCE(usage.cost_usd, 0),
                   debits.lease_token IS NOT NULL,
                   COALESCE(debits.debit, 0)
            FROM usage_events usage
            FULL OUTER JOIN debits ON debits.lease_token = usage.lease_token
            LEFT JOIN request_leases lease
              ON lease.lease_token = COALESCE(usage.lease_token, debits.lease_token)
            WHERE COALESCE(usage.cost_usd, 0) <> COALESCE(debits.debit, 0)
            ORDER BY COALESCE(usage.lease_token, debits.lease_token)
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var leaseToken = reader.GetString(0);
            var usageExists = reader.GetBoolean(1);
            var userId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            var usageCost = reader.GetDecimal(3);
            var debitExists = reader.GetBoolean(4);
            var debit = reader.GetDecimal(5);
            mismatchTotal += Math.Abs(usageCost - debit);
            var kind = !usageExists ? "usage_debit_without_usage"
                : !debitExists ? "usage_debit_missing"
                : "usage_debit_mismatch";
            anomalies.Add(new ReconciliationAnomaly(
                    $"usage-debit:{leaseToken}", kind, "critical", userId, leaseToken,
                    new { usage_cost_equals_debit = true, usage_cost = usageCost },
                    new { usage_exists = usageExists, debit_exists = debitExists, debit }));
        }
        await reader.DisposeAsync();
        foreach (var anomaly in anomalies)
            await UpsertIncidentAsync(connection, runId, anomaly, ct);
        return mismatchTotal;
    }

    private async Task ScanLeaseHoldsAsync(
        NpgsqlConnection connection,
        long runId,
        CancellationToken ct)
    {
        var anomalies = new List<ReconciliationAnomaly>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT 'hold_without_lease', hold.hold_id, hold.user_id,
                       hold.lease_token, hold.status, hold.amount, NULL::text
                FROM balance_holds hold
                LEFT JOIN request_leases lease ON lease.lease_token = hold.lease_token
                WHERE hold.status = 'active' AND lease.lease_token IS NULL
                UNION ALL
                SELECT 'lease_hold_missing', lease.lease_token, lease.user_id,
                       lease.lease_token, COALESCE(hold.status, 'missing'),
                       lease.hold_amount, lease.status
                FROM request_leases lease
                LEFT JOIN balance_holds hold ON hold.hold_id = lease.hold_handle
                WHERE lease.status IN ('held', 'forwarded', 'output_started')
                  AND lease.hold_amount > 0
                  AND (hold.hold_id IS NULL OR hold.status <> 'active')
                UNION ALL
                SELECT 'terminal_hold_mismatch', lease.lease_token, lease.user_id,
                       lease.lease_token, COALESCE(hold.status, 'missing'),
                       lease.hold_amount, lease.status
                FROM request_leases lease
                LEFT JOIN balance_holds hold ON hold.hold_id = lease.hold_handle
                WHERE lease.status IN ('completed', 'aborted', 'expired')
                  AND lease.hold_amount > 0
                  AND (hold.hold_id IS NULL
                    OR (lease.status = 'completed' AND hold.status <> 'committed')
                    OR (lease.status IN ('aborted', 'expired') AND hold.status <> 'released'))
                UNION ALL
                SELECT 'unknown_provider_charge', lease.lease_token, lease.user_id,
                       lease.lease_token, COALESCE(hold.status, 'missing'),
                       lease.hold_amount, lease.status
                FROM request_leases lease
                LEFT JOIN balance_holds hold ON hold.hold_id = lease.hold_handle
                WHERE lease.status = 'reconciliation_needed'
                UNION ALL
                SELECT 'expired_open_lease', lease.lease_token, lease.user_id,
                       lease.lease_token, COALESCE(hold.status, 'missing'),
                       lease.hold_amount, lease.status
                FROM request_leases lease
                LEFT JOIN balance_holds hold ON hold.hold_id = lease.hold_handle
                WHERE lease.status IN ('held', 'forwarded', 'output_started')
                  AND lease.expires_at < now() - interval '30 seconds'
                ORDER BY 1, 2
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var kind = reader.GetString(0);
                var identity = reader.GetString(1);
                var userId = reader.GetInt64(2);
                var leaseToken = reader.IsDBNull(3) ? null : reader.GetString(3);
                var holdStatus = reader.GetString(4);
                var holdAmount = reader.GetDecimal(5);
                var leaseStatus = reader.IsDBNull(6) ? null : reader.GetString(6);
                var expectedHoldStatus = leaseStatus switch
                {
                    "completed" => "committed",
                    "aborted" or "expired" => "released",
                    _ => "active",
                };
                anomalies.Add(new(
                    $"{kind}:{identity}", kind, "critical", userId, leaseToken,
                    new
                    {
                        hold_status = expectedHoldStatus,
                        resolution = kind == "unknown_provider_charge"
                            ? "late_usage_or_operator_decision" : "restore_invariant",
                    },
                    new { hold_status = holdStatus, hold_amount = holdAmount, lease_status = leaseStatus }));
            }
        }

        foreach (var anomaly in anomalies)
            await UpsertIncidentAsync(connection, runId, anomaly, ct);
    }

    private async Task<long> ReconcileProjectionsAsync(
        NpgsqlConnection connection,
        long runId,
        IReadOnlyList<AccountingSnapshot> snapshots,
        IReadOnlySet<long> invalidUsers,
        CancellationToken ct)
    {
        long repaired = 0;
        const int batchSize = 16;
        for (var offset = 0; offset < snapshots.Count; offset += batchSize)
        {
            var batch = snapshots.Skip(offset).Take(batchSize)
                .Where(snapshot => !invalidUsers.Contains(snapshot.UserId))
                .ToArray();
            var outcomes = await Task.WhenAll(batch.Select(async snapshot =>
                (Snapshot: snapshot,
                 Result: await projectionRepairer.RepairAsync(snapshot, ct))));
            foreach (var outcome in outcomes)
            {
                if (outcome.Result.State == ProjectionRepairState.Repaired)
                {
                    repaired++;
                    await MarkProjectionCurrentAsync(connection, outcome.Snapshot, ct);
                    continue;
                }
                if (outcome.Result.State == ProjectionRepairState.Consistent)
                {
                    await MarkProjectionCurrentAsync(connection, outcome.Snapshot, ct);
                    continue;
                }

                var kind = outcome.Result.ActualVersion > outcome.Snapshot.Version
                    ? "grain_projection_ahead" : "grain_projection_unavailable";
                await UpsertIncidentAsync(connection, runId,
                    new ReconciliationAnomaly(
                        $"grain-projection:{outcome.Snapshot.UserId}", kind, "critical",
                        outcome.Snapshot.UserId, null,
                        new
                        {
                            version = outcome.Snapshot.Version,
                            balance = outcome.Snapshot.Balance,
                        },
                        new
                        {
                            version = outcome.Result.ActualVersion,
                            balance = outcome.Result.ActualBalance,
                            error = outcome.Result.Error,
                        }), ct);
            }
        }
        return repaired;
    }

    private static async Task MarkProjectionCurrentAsync(
        NpgsqlConnection connection,
        AccountingSnapshot snapshot,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM accounting_projection_outbox
            WHERE user_id = $1 AND ledger_version <= $2
            """;
        command.Parameters.AddWithValue(snapshot.UserId);
        command.Parameters.AddWithValue(snapshot.Version);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertIncidentAsync(
        NpgsqlConnection connection,
        long runId,
        ReconciliationAnomaly anomaly,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounting_reconciliation_incidents(
                incident_key, kind, severity, user_id, lease_token, status,
                expected, actual, last_run_id)
            VALUES ($1, $2, $3, $4, $5, 'open', $6, $7, $8)
            ON CONFLICT (incident_key) DO UPDATE
            SET kind = EXCLUDED.kind,
                severity = EXCLUDED.severity,
                user_id = EXCLUDED.user_id,
                lease_token = EXCLUDED.lease_token,
                status = 'open',
                expected = EXCLUDED.expected,
                actual = EXCLUDED.actual,
                occurrences = accounting_reconciliation_incidents.occurrences + 1,
                last_seen_at = now(),
                resolved_at = NULL,
                last_run_id = EXCLUDED.last_run_id
            """;
        command.Parameters.AddWithValue(anomaly.Key);
        command.Parameters.AddWithValue(anomaly.Kind);
        command.Parameters.AddWithValue(anomaly.Severity);
        command.Parameters.AddWithValue((object?)anomaly.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)anomaly.LeaseToken ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = JsonSerializer.Serialize(anomaly.Expected),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = JsonSerializer.Serialize(anomaly.Actual),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        command.Parameters.AddWithValue(runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ResolveUnseenIncidentsAsync(
        NpgsqlConnection connection,
        long runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE accounting_reconciliation_incidents
            SET status = 'resolved', resolved_at = now()
            WHERE status = 'open' AND last_run_id IS DISTINCT FROM $1
            """;
        command.Parameters.AddWithValue(runId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountOpenIncidentsAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM accounting_reconciliation_incidents WHERE status = 'open'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<decimal> ReadDecimalAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToDecimal(await command.ExecuteScalarAsync(ct));
    }

    private static async Task CompleteRunAsync(
        NpgsqlConnection connection,
        long runId,
        string status,
        long checkedAccounts,
        long repairedHolds,
        long repairedProjections,
        long openIncidents,
        long resolvedIncidents,
        decimal ledgerTotal,
        decimal holdTotal,
        decimal mismatchTotal,
        string trigger,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ledger_reconciliation_runs
            SET completed_at = now(), status = $2,
                ledger_total = $3, hold_total = $4, mismatch_total = $5,
                checked_accounts = $6, repaired_holds = $7,
                repaired_projections = $8, open_incidents = $9,
                resolved_incidents = $10,
                details = jsonb_build_object('trigger', $11)
            WHERE id = $1
            """;
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(ledgerTotal);
        command.Parameters.AddWithValue(holdTotal);
        command.Parameters.AddWithValue(mismatchTotal);
        command.Parameters.AddWithValue(checkedAccounts);
        command.Parameters.AddWithValue(repairedHolds);
        command.Parameters.AddWithValue(repairedProjections);
        command.Parameters.AddWithValue(openIncidents);
        command.Parameters.AddWithValue(resolvedIncidents);
        command.Parameters.AddWithValue(trigger);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task FailRunAsync(
        NpgsqlConnection connection,
        long runId,
        Exception error,
        CancellationToken ct)
    {
        var message = error.Message.Length > 1000 ? error.Message[..1000] : error.Message;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ledger_reconciliation_runs
            SET completed_at = now(), status = 'failed',
                details = details || jsonb_build_object('failure', $2)
            WHERE id = $1
            """;
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(message);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record AccountingScan(
        IReadOnlyList<AccountingSnapshot> Snapshots,
        IReadOnlySet<long> InvalidUsers,
        decimal MismatchTotal);

    private sealed record ReconciliationAnomaly(
        string Key,
        string Kind,
        string Severity,
        long? UserId,
        string? LeaseToken,
        object Expected,
        object Actual);
}
