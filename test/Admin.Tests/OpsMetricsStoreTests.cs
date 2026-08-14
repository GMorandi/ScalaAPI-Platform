using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class OpsMetricsStoreTests
{
    [Fact]
    public async Task MetricsAreAuditedBoundedAndAggregatedWithPolicyAlerts()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var actorId = 9_000_000L + Random.Shared.Next(1, 900_000);
        var metricName = $"test.ops.{Guid.NewGuid():N}";
        var eventKey = $"test-alert-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new OpsMetricsStore(dataSource);
        try
        {
            var first = await store.RecordAsync(actorId, metricName, 2.5m,
                "{\"source\":\"test\"}", "127.0.0.1");
            var second = await store.RecordAsync(actorId, metricName, 7.5m, null, null);
            var invalid = await store.RecordAsync(actorId, "bad metric", 1m, null, null);

            Assert.Equal(OpsMetricWriteStatus.Created, first.Status);
            Assert.Equal(OpsMetricWriteStatus.Created, second.Status);
            Assert.Equal(OpsMetricWriteStatus.Invalid, invalid.Status);

            await using (var alert = dataSource.CreateCommand("""
                INSERT INTO content_policy_alert_events(
                    event_key, kind, severity, stage, code, policy_revision, details)
                VALUES ($1, 'classifier_unavailable', 'critical', 'request',
                        'test_unavailable', 1, '{}'::jsonb)
                """))
            {
                alert.Parameters.AddWithValue(eventKey);
                await alert.ExecuteNonQueryAsync();
            }

            var summary = await store.SummarizeAsync(metricName, null, null, 10);
            var alerts = await store.ListPolicyAlertsAsync(
                "classifier_unavailable", "critical", null, 10);
            Assert.Single(summary);
            Assert.Equal(7.5m, summary[0].LatestValue);
            Assert.Equal(5m, summary[0].AverageValue);
            Assert.Equal(2, summary[0].Samples);
            Assert.Single(alerts);
            Assert.Equal(eventKey, alerts[0].EventKey);

            await using var audit = dataSource.CreateCommand("""
                SELECT count(*) FROM audit_logs
                WHERE user_id = $1 AND action = 'ops.metric.recorded'
                  AND resource_type = 'ops_metric'
                """);
            audit.Parameters.AddWithValue(actorId);
            Assert.Equal(2L, Convert.ToInt64(await audit.ExecuteScalarAsync()));
        }
        finally
        {
            foreach (var statement in new[]
            {
                "DELETE FROM audit_logs WHERE user_id = $1 AND action = 'ops.metric.recorded'",
                "DELETE FROM ops_metrics WHERE metric_name = $1",
                "DELETE FROM content_policy_alert_events WHERE event_key = $1",
            })
            {
                await using var cleanup = dataSource.CreateCommand(statement);
                cleanup.Parameters.AddWithValue(statement.Contains("event_key", StringComparison.Ordinal)
                    ? eventKey : statement.Contains("metric_name", StringComparison.Ordinal)
                        ? metricName : actorId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
