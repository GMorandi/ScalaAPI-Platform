using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace ScalaAPI.Admin.Data;

public enum OpsMetricWriteStatus
{
    Created,
    Invalid,
}

public sealed record OpsMetricWriteResult(
    OpsMetricWriteStatus Status,
    long? Id,
    string? Error);

public sealed record OpsMetricSummary(
    string MetricName,
    decimal LatestValue,
    decimal AverageValue,
    long Samples,
    DateTime LatestAt);

public sealed record OpsPolicyAlert(
    long Id,
    string EventKey,
    string Kind,
    string Severity,
    long? RuleId,
    long? UserId,
    string? RequestId,
    string Stage,
    string Code,
    long PolicyRevision,
    string Details,
    DateTime CreatedAt);

public sealed class OpsMetricsStore(NpgsqlDataSource dataSource)
{
    private static readonly Regex MetricNamePattern = new(
        "^[A-Za-z0-9_.:-]{1,120}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<OpsMetricWriteResult> RecordAsync(
        long actorId,
        string? metricName,
        decimal metricValue,
        string? labels,
        string? clientIp,
        CancellationToken ct = default)
    {
        metricName = metricName?.Trim();
        labels = string.IsNullOrWhiteSpace(labels) ? null : labels.Trim();
        if (actorId <= 0)
            return new(OpsMetricWriteStatus.Invalid, null, "actor_id must be positive");
        if (string.IsNullOrWhiteSpace(metricName) || !MetricNamePattern.IsMatch(metricName))
            return new(OpsMetricWriteStatus.Invalid, null, "metric_name must use bounded identifier characters");
        if (labels is not null && labels.Length > 2_000)
            return new(OpsMetricWriteStatus.Invalid, null, "labels is too long");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ops_metrics(metric_name, metric_value, labels)
            VALUES ($1, $2, $3)
            RETURNING id
            """;
        insert.Parameters.AddWithValue(metricName);
        insert.Parameters.AddWithValue(metricValue);
        insert.Parameters.AddWithValue((object?)labels ?? DBNull.Value);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));

        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(
                user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, 'ops.metric.recorded', 'ops_metric', $2, $3, $4)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(id.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue(JsonSerializer.Serialize(new
        {
            metric_name = metricName,
            metric_value = metricValue,
            labels,
        }));
        audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return new(OpsMetricWriteStatus.Created, id, null);
    }

    public async Task<IReadOnlyList<OpsMetricSummary>> SummarizeAsync(
        string? metricName,
        DateTime? from,
        DateTime? to,
        int limit,
        CancellationToken ct = default)
    {
        metricName = string.IsNullOrWhiteSpace(metricName) ? null : metricName.Trim();
        limit = Math.Clamp(limit, 1, 100);
        await using var command = dataSource.CreateCommand("""
            SELECT m.metric_name,
                   (SELECT latest.metric_value
                    FROM ops_metrics latest
                    WHERE latest.metric_name = m.metric_name
                      AND ($1::text IS NULL OR latest.metric_name = $1)
                      AND ($2::timestamptz IS NULL OR latest.collected_at >= $2)
                      AND ($3::timestamptz IS NULL OR latest.collected_at <= $3)
                    ORDER BY latest.collected_at DESC, latest.id DESC
                    LIMIT 1) AS latest_value,
                   avg(m.metric_value) AS average_value,
                   count(*) AS samples,
                   max(m.collected_at) AS latest_at
            FROM ops_metrics m
            WHERE ($1::text IS NULL OR m.metric_name = $1)
              AND ($2::timestamptz IS NULL OR m.collected_at >= $2)
              AND ($3::timestamptz IS NULL OR m.collected_at <= $3)
            GROUP BY m.metric_name
            ORDER BY latest_at DESC, m.metric_name
            LIMIT $4
            """);
        command.Parameters.AddWithValue((object?)metricName ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue(limit);

        var items = new List<OpsMetricSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new OpsMetricSummary(
                reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2),
                reader.GetInt64(3), reader.GetDateTime(4)));
        }
        return items;
    }

    public async Task<IReadOnlyList<OpsPolicyAlert>> ListPolicyAlertsAsync(
        string? kind,
        string? severity,
        DateTime? from,
        int limit,
        CancellationToken ct = default)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        severity = string.IsNullOrWhiteSpace(severity) ? null : severity.Trim();
        limit = Math.Clamp(limit, 1, 100);
        await using var command = dataSource.CreateCommand("""
            SELECT id, event_key, kind, severity, rule_id, user_id, request_id,
                   stage, code, policy_revision, details::text, created_at
            FROM content_policy_alert_events
            WHERE ($1::text IS NULL OR kind = $1)
              AND ($2::text IS NULL OR severity = $2)
              AND ($3::timestamptz IS NULL OR created_at >= $3)
            ORDER BY created_at DESC, id DESC
            LIMIT $4
            """);
        command.Parameters.AddWithValue((object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)severity ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue(limit);

        var items = new List<OpsPolicyAlert>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new OpsPolicyAlert(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt64(9), reader.GetString(10),
                reader.GetDateTime(11)));
        }
        return items;
    }
}
