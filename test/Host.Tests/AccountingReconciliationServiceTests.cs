using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Host.Services;

namespace ScalaAPI.Host.Tests;

public sealed class AccountingReconciliationServiceTests
{
    [Fact]
    public async Task ReconciliationRepairsSafeDriftPersistsIncidentsAndResolvesThem()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var projectionUserId = Random.Shared.NextInt64(100_000_000, 500_000_000);
        var mismatchUserId = projectionUserId + 1;
        var repairedLease = $"lease-reconcile-repair-{suffix}";
        var unknownLease = $"lease-reconcile-unknown-{suffix}";
        var repairedHold = $"hold-reconcile-repair-{suffix}";
        var unknownHold = $"hold-reconcile-unknown-{suffix}";
        var runIds = new List<long>();
        var accounting = new AccountingStore(dataSource);
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "1",
            }).Build());
        var leases = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        var repairer = new RecordingProjectionRepairer(projectionUserId);
        var reconciliation = new AccountingReconciliationService(
            dataSource, repairer, NullLogger<AccountingReconciliationService>.Instance);

        try
        {
            await accounting.AppendEffectAsync(new AccountingEffect(
                projectionUserId, $"test-funding:{suffix}:projection",
                "test_credit", 10m));
            await accounting.AppendEffectAsync(new AccountingEffect(
                mismatchUserId, $"test-funding:{suffix}:mismatch",
                "test_credit", 5m));

            Assert.True(await leases.CreateAsync(NewLease(
                repairedLease, projectionUserId, repairedHold,
                DateTime.UtcNow.AddMinutes(10), suffix)));
            Assert.True((await leases.CompleteAsync(new LeaseCompletion(
                repairedLease, 100, 0, 0, 0, 10, 0, 200, false, false))).Accepted);
            await using (var corruptHold = dataSource.CreateCommand("""
                UPDATE balance_holds
                SET status = 'active', finalized_at = NULL
                WHERE hold_id = $1
                """))
            {
                corruptHold.Parameters.AddWithValue(repairedHold);
                Assert.Equal(1, await corruptHold.ExecuteNonQueryAsync());
            }

            Assert.True(await leases.CreateAsync(NewLease(
                unknownLease, projectionUserId, unknownHold,
                DateTime.UtcNow.AddMinutes(-1), suffix)));
            Assert.Equal(1, await leases.ExpireActiveAsync());

            await using (var corruptAccount = dataSource.CreateCommand("""
                UPDATE accounting_accounts
                SET posted_balance = posted_balance + 2
                WHERE user_id = $1
                """))
            {
                corruptAccount.Parameters.AddWithValue(mismatchUserId);
                Assert.Equal(1, await corruptAccount.ExecuteNonQueryAsync());
            }

            var failed = await reconciliation.RunAsync("test-first");
            Assert.True(failed.Started);
            Assert.Equal("failed", failed.Status);
            Assert.Equal(1, failed.RepairedHolds);
            Assert.True(failed.RepairedProjections >= 1);
            Assert.True(failed.OpenIncidents >= 2);
            Assert.Equal(2m, failed.MismatchTotal);
            runIds.Add(failed.RunId!.Value);
            Assert.Equal("committed", await ReadTextAsync(dataSource,
                "SELECT status FROM balance_holds WHERE hold_id = $1", repairedHold));
            Assert.Equal("active", await ReadTextAsync(dataSource,
                "SELECT status FROM balance_holds WHERE hold_id = $1", unknownHold));
            Assert.Equal("reconciliation_needed", await ReadTextAsync(dataSource,
                "SELECT status FROM request_leases WHERE lease_token = $1", unknownLease));
            Assert.Equal("open", await ReadIncidentStatusAsync(
                dataSource, $"account-ledger:{mismatchUserId}"));
            Assert.Equal("open", await ReadIncidentStatusAsync(
                dataSource, $"unknown_provider_charge:{unknownLease}"));

            await using (var repairAccount = dataSource.CreateCommand("""
                UPDATE accounting_accounts account
                SET posted_balance = ledger.balance
                FROM (
                    SELECT user_id, sum(amount) AS balance
                    FROM balance_ledger WHERE user_id = $1 GROUP BY user_id
                ) ledger
                WHERE account.user_id = ledger.user_id
                """))
            {
                repairAccount.Parameters.AddWithValue(mismatchUserId);
                Assert.Equal(1, await repairAccount.ExecuteNonQueryAsync());
            }
            Assert.True((await leases.CompleteAsync(new LeaseCompletion(
                unknownLease, 50, 0, 0, 0, 10, 0, 200, false, false))).Accepted);

            var passed = await reconciliation.RunAsync("test-second");
            Assert.True(passed.Started);
            Assert.Equal("passed", passed.Status);
            Assert.Equal(0, passed.OpenIncidents);
            Assert.True(passed.ResolvedIncidents >= 2);
            Assert.Equal(0m, passed.MismatchTotal);
            runIds.Add(passed.RunId!.Value);
            Assert.Equal("resolved", await ReadIncidentStatusAsync(
                dataSource, $"account-ledger:{mismatchUserId}"));
            Assert.Equal("resolved", await ReadIncidentStatusAsync(
                dataSource, $"unknown_provider_charge:{unknownLease}"));
            Assert.Equal("completed", await ReadTextAsync(dataSource,
                "SELECT status FROM request_leases WHERE lease_token = $1", unknownLease));
            Assert.Equal("committed", await ReadTextAsync(dataSource,
                "SELECT status FROM balance_holds WHERE hold_id = $1", unknownHold));
        }
        finally
        {
            await DeleteByLeasesAsync(dataSource, "usage_outbox", [repairedLease, unknownLease]);
            await DeleteByLeasesAsync(dataSource, "usage_logs", [repairedLease, unknownLease]);
            await DeleteByLeasesAsync(dataSource, "usage_events", [repairedLease, unknownLease]);
            await DeleteByLeasesAsync(dataSource, "balance_ledger", [repairedLease, unknownLease]);
            await using (var incidents = dataSource.CreateCommand("""
                DELETE FROM accounting_reconciliation_incidents
                WHERE user_id IN ($1, $2)
                """))
            {
                incidents.Parameters.AddWithValue(projectionUserId);
                incidents.Parameters.AddWithValue(mismatchUserId);
                await incidents.ExecuteNonQueryAsync();
            }
            await using (var holds = dataSource.CreateCommand("""
                DELETE FROM balance_holds WHERE hold_id IN ($1, $2)
                """))
            {
                holds.Parameters.AddWithValue(repairedHold);
                holds.Parameters.AddWithValue(unknownHold);
                await holds.ExecuteNonQueryAsync();
            }
            await using (var leaseRows = dataSource.CreateCommand("""
                DELETE FROM request_leases WHERE lease_token IN ($1, $2)
                """))
            {
                leaseRows.Parameters.AddWithValue(repairedLease);
                leaseRows.Parameters.AddWithValue(unknownLease);
                await leaseRows.ExecuteNonQueryAsync();
            }
            foreach (var table in new[]
                     {
                         "accounting_projection_outbox", "balance_ledger", "accounting_accounts"
                     })
            {
                await using var accounts = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE user_id IN ($1, $2)");
                accounts.Parameters.AddWithValue(projectionUserId);
                accounts.Parameters.AddWithValue(mismatchUserId);
                await accounts.ExecuteNonQueryAsync();
            }
            if (runIds.Count > 0)
            {
                await using var runs = dataSource.CreateCommand(
                    "DELETE FROM ledger_reconciliation_runs WHERE id = ANY($1)");
                runs.Parameters.AddWithValue(runIds.ToArray());
                await runs.ExecuteNonQueryAsync();
            }
        }
    }

    private static LeaseCreateRequest NewLease(
        string leaseToken,
        long userId,
        string holdId,
        DateTime expiresAt,
        string suffix) =>
        new(leaseToken, $"request-{leaseToken}", "hash-reconciliation", 700_001,
            userId, 700_002, 700_003, "gpt-4o", "gpt-4o", "chat_completions",
            1m, holdId, 1m, expiresAt,
            $"idempotency-{leaseToken}-{suffix}", $"fingerprint-{leaseToken}");

    private static async Task<string> ReadTextAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string value)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(value);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static Task<string> ReadIncidentStatusAsync(
        NpgsqlDataSource dataSource,
        string incidentKey) =>
        ReadTextAsync(dataSource,
            "SELECT status FROM accounting_reconciliation_incidents WHERE incident_key = $1",
            incidentKey);

    private static async Task DeleteByLeasesAsync(
        NpgsqlDataSource dataSource,
        string table,
        string[] leaseTokens)
    {
        await using var command = dataSource.CreateCommand(
            $"DELETE FROM {table} WHERE lease_token = ANY($1)");
        command.Parameters.AddWithValue(leaseTokens);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingProjectionRepairer(long staleUserId)
        : IAccountingProjectionRepairer
    {
        private readonly Dictionary<long, AccountingSnapshot> _state = [];

        public Task<ProjectionRepairResult> RepairAsync(
            AccountingSnapshot expected,
            CancellationToken ct = default)
        {
            if (!_state.TryGetValue(expected.UserId, out var actual))
            {
                actual = expected.UserId == staleUserId
                    ? new(expected.UserId, 0, 0m)
                    : expected;
            }
            if (actual.Version == expected.Version && actual.Balance == expected.Balance)
                return Task.FromResult(new ProjectionRepairResult(
                    ProjectionRepairState.Consistent, actual.Version, actual.Balance));

            _state[expected.UserId] = expected;
            return Task.FromResult(new ProjectionRepairResult(
                ProjectionRepairState.Repaired, expected.Version, expected.Balance));
        }
    }
}
