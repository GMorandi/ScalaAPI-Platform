using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Host.Services;

namespace ScalaAPI.Host.Tests;

public sealed class ReconciliationResolutionTests
{
    [Fact]
    public void DifferentActorsProduceDifferentFingerprints()
    {
        var request = new ReconciliationResolutionRequest(
            "release", "provider_rejection", "Provider HTTP 429 receipt",
            "Provider rejected the request", StatusCode: 429);

        var fingerprintA = ReconciliationResolutionFingerprint.Compute(1, request, actorUserId: 100);
        var fingerprintB = ReconciliationResolutionFingerprint.Compute(1, request, actorUserId: 200);

        Assert.NotEqual(fingerprintA, fingerprintB);

        var fingerprintSame = ReconciliationResolutionFingerprint.Compute(1, request, actorUserId: 100);
        Assert.Equal(fingerprintA, fingerprintSame);
    }

    [Fact]
    public async Task OperatorSettleAndReleaseAreAtomicAndIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var userId = Random.Shared.NextInt64(600_000_000, 700_000_000);
        var actorId = userId + 1;
        var accounting = new AccountingStore(dataSource);
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "1",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "2",
            }).Build());
        var leases = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        var settleLease = $"lease-resolution-settle-{suffix}";
        var releaseLease = $"lease-resolution-release-{suffix}";
        var raceLease = $"lease-resolution-race-{suffix}";
        var holdIds = new[]
        {
            $"hold-resolution-settle-{suffix}",
            $"hold-resolution-release-{suffix}",
            $"hold-resolution-race-{suffix}",
        };
        var incidentIds = new List<long>();

        try
        {
            await accounting.AppendEffectAsync(new AccountingEffect(
                userId, $"resolution-funding:{suffix}", "test_credit", 100m));

            await CreateUnknownAsync(leases, settleLease, userId, holdIds[0], suffix);
            var settleIncident = await InsertIncidentAsync(dataSource, settleLease, userId);
            incidentIds.Add(settleIncident);
            var settleRequest = new ReconciliationResolutionRequest(
                "settle", "provider_usage", "provider usage export ref usage-1",
                "Provider invoice matched the request", InputTokens: 100,
                OutputTokens: 20, StatusCode: 200);

            var settled = await leases.ResolveReconciliationAsync(
                settleIncident, actorId, $"resolve-settle-{suffix}", settleRequest,
                "127.0.0.1");
            Assert.Equal(ReconciliationResolutionStatus.Applied, settled.Status);
            Assert.Equal(0.00014m, settled.CostUsd);
            Assert.Equal("completed", await ReadAsync(dataSource,
                "SELECT status FROM request_leases WHERE lease_token = $1", settleLease));
            Assert.Equal("committed", await ReadAsync(dataSource,
                "SELECT status FROM balance_holds WHERE hold_id = $1", holdIds[0]));
            Assert.Equal(1L, await CountAsync(dataSource,
                "SELECT count(*) FROM usage_events WHERE lease_token = $1", settleLease));
            Assert.Equal(1L, await CountAsync(dataSource,
                "SELECT count(*) FROM balance_ledger WHERE lease_token = $1 AND entry_type = 'usage_debit'",
                settleLease));
            Assert.Equal("resolved", await ReadAsync(dataSource,
                "SELECT status FROM accounting_reconciliation_incidents WHERE id = $1", settleIncident));
            Assert.Equal(1L, await CountAsync(dataSource,
                "SELECT count(*) FROM accounting_reconciliation_resolutions WHERE incident_id = $1",
                settleIncident));
            Assert.Equal(1L, await CountAsync(dataSource,
                "SELECT count(*) FROM audit_logs WHERE action = 'reconciliation.resolve' AND resource_id = $1",
                settleLease));
            Assert.Equal(1L, await CountAsync(dataSource,
                "SELECT count(*) FROM request_lease_events WHERE lease_token = $1 AND event_type = 'completed' AND source = 'operator'",
                settleLease));

            var duplicate = await leases.ResolveReconciliationAsync(
                settleIncident, actorId, $"resolve-settle-{suffix}", settleRequest);
            Assert.Equal(ReconciliationResolutionStatus.Duplicate, duplicate.Status);
            var conflict = await leases.ResolveReconciliationAsync(
                settleIncident, actorId, $"resolve-settle-{suffix}", settleRequest with
                { OutputTokens = 21 });
            Assert.Equal(ReconciliationResolutionStatus.Conflict, conflict.Status);
            Assert.Equal(1L, await CountAsync(dataSource,
                "SELECT count(*) FROM balance_ledger WHERE lease_token = $1 AND entry_type = 'usage_debit'",
                settleLease));

            await CreateUnknownAsync(leases, releaseLease, userId, holdIds[1], suffix);
            var releaseIncident = await InsertIncidentAsync(dataSource, releaseLease, userId);
            incidentIds.Add(releaseIncident);
            var invalidRelease = await leases.ResolveReconciliationAsync(
                releaseIncident, actorId, $"resolve-release-invalid-{suffix}",
                new("release", "operator_usage_review", "not no-charge proof", "review"));
            Assert.Equal(ReconciliationResolutionStatus.Invalid, invalidRelease.Status);
            Assert.Equal("reconciliation_needed", await ReadAsync(dataSource,
                "SELECT status FROM request_leases WHERE lease_token = $1", releaseLease));

            var released = await leases.ResolveReconciliationAsync(
                releaseIncident, actorId, $"resolve-release-{suffix}", new(
                    "release", "provider_rejection", "Provider HTTP 429 receipt ref reject-1",
                    "Provider confirmed the request was rejected before charge", StatusCode: 429));
            Assert.Equal(ReconciliationResolutionStatus.Applied, released.Status);
            Assert.Equal("aborted", await ReadAsync(dataSource,
                "SELECT status FROM request_leases WHERE lease_token = $1", releaseLease));
            Assert.Equal("released", await ReadAsync(dataSource,
                "SELECT status FROM balance_holds WHERE hold_id = $1", holdIds[1]));
            Assert.Equal(0L, await CountAsync(dataSource,
                "SELECT count(*) FROM usage_events WHERE lease_token = $1", releaseLease));
            Assert.Equal(0L, await CountAsync(dataSource,
                "SELECT count(*) FROM balance_ledger WHERE lease_token = $1", releaseLease));

            await CreateUnknownAsync(leases, raceLease, userId, holdIds[2], suffix);
            var raceIncident = await InsertIncidentAsync(dataSource, raceLease, userId);
            incidentIds.Add(raceIncident);
            var raceRequest = new ReconciliationResolutionRequest(
                "release", "provider_confirmed_no_charge", "Provider confirmed no charge for the request",
                "Release after explicit Provider no-charge confirmation");
            var race = await Task.WhenAll(
                leases.ResolveReconciliationAsync(raceIncident, actorId,
                    $"resolve-race-a-{suffix}", raceRequest),
                leases.ResolveReconciliationAsync(raceIncident, actorId,
                    $"resolve-race-b-{suffix}", raceRequest));
            Assert.Equal(1, race.Count(result =>
                result.Status == ReconciliationResolutionStatus.Applied));
            Assert.Equal(1, race.Count(result =>
                result.Status == ReconciliationResolutionStatus.Invalid
                    && result.ErrorCode == "incident_already_resolved"));
            Assert.Equal(1L, await CountAsync(dataSource,
                "SELECT count(*) FROM accounting_reconciliation_resolutions WHERE incident_id = $1",
                raceIncident));
        }
        finally
        {
            foreach (var table in new[] { "usage_outbox", "usage_logs", "usage_events", "balance_ledger" })
            {
                await using var cleanup = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE lease_token IN ($1, $2, $3)");
                cleanup.Parameters.AddWithValue(settleLease);
                cleanup.Parameters.AddWithValue(releaseLease);
                cleanup.Parameters.AddWithValue(raceLease);
                await cleanup.ExecuteNonQueryAsync();
            }
            if (incidentIds.Count > 0)
            {
                await using var resolutions = dataSource.CreateCommand(
                    "DELETE FROM accounting_reconciliation_resolutions WHERE incident_id = ANY($1)");
                resolutions.Parameters.AddWithValue(incidentIds.ToArray());
                await resolutions.ExecuteNonQueryAsync();
                await using var incidents = dataSource.CreateCommand(
                    "DELETE FROM accounting_reconciliation_incidents WHERE id = ANY($1)");
                incidents.Parameters.AddWithValue(incidentIds.ToArray());
                await incidents.ExecuteNonQueryAsync();
            }
            await using (var audit = dataSource.CreateCommand(
                "DELETE FROM audit_logs WHERE user_id = $1 AND action = 'reconciliation.resolve'"))
            {
                audit.Parameters.AddWithValue(actorId);
                await audit.ExecuteNonQueryAsync();
            }
            foreach (var holdId in holdIds)
            {
                await using var hold = dataSource.CreateCommand(
                    "DELETE FROM balance_holds WHERE hold_id = $1");
                hold.Parameters.AddWithValue(holdId);
                await hold.ExecuteNonQueryAsync();
            }
            foreach (var lease in new[] { settleLease, releaseLease, raceLease })
            {
                await using var leaseRow = dataSource.CreateCommand(
                    "DELETE FROM request_leases WHERE lease_token = $1");
                leaseRow.Parameters.AddWithValue(lease);
                await leaseRow.ExecuteNonQueryAsync();
            }
            foreach (var table in new[] { "accounting_projection_outbox", "balance_ledger", "accounting_accounts" })
            {
                await using var account = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE user_id = $1");
                account.Parameters.AddWithValue(userId);
                await account.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task CreateUnknownAsync(
        RequestLeaseStore leases, string leaseToken, long userId, string holdId, string suffix)
    {
        Assert.True(await leases.CreateAsync(new LeaseCreateRequest(
            leaseToken, $"request-{leaseToken}", "hash-resolution", 880001,
            userId, 880002, 880003, "gpt-4o", "gpt-4o", "chat_completions",
            1m, holdId, 10m, DateTime.UtcNow.AddMinutes(10),
            $"idempotency-{leaseToken}-{suffix}", $"fingerprint-{leaseToken}")));
        Assert.True((await leases.RecordEvidenceAsync(
            leaseToken, LeaseEvidenceStage.Forwarded)).Accepted);
        Assert.True((await leases.AbortAsync(
            leaseToken, "provider_transport_lost", LeaseAbortDisposition.Unknown)).Accepted);
    }

    private static async Task<long> InsertIncidentAsync(
        NpgsqlDataSource dataSource, string leaseToken, long userId)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO accounting_reconciliation_incidents(
                incident_key, kind, severity, user_id, lease_token, status, expected, actual)
            VALUES ($1, 'unknown_provider_charge', 'critical', $2, $3, 'open', '{}'::jsonb, '{}'::jsonb)
            RETURNING id
            """);
        command.Parameters.AddWithValue($"test-resolution:{leaseToken}");
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(leaseToken);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadAsync(
        NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue(values[i]);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<long> CountAsync(
        NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue(values[i]);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
