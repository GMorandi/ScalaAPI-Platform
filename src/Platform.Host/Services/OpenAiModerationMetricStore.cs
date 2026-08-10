using Npgsql;
using NpgsqlTypes;

namespace ScalaAPI.Host.Services;

public sealed record OpenAiModerationMetricSnapshot(
    Guid InstanceId,
    long Sequence,
    long Requests,
    long Matches,
    long NoMatches,
    long Unavailable,
    long ProtocolErrors,
    long Cancellations,
    long DurationTicks,
    long[] Buckets);

public sealed record OpenAiModerationMetricTotals(
    long Requests,
    long Matches,
    long NoMatches,
    long Unavailable,
    long ProtocolErrors,
    long Cancellations,
    long DurationTicks,
    long[] Buckets)
{
    public static OpenAiModerationMetricTotals Empty =>
        new(0, 0, 0, 0, 0, 0, 0, new long[10]);

    public OpenAiModerationMetricTotals Add(OpenAiModerationMetricSnapshot snapshot) =>
        new(
            Requests + snapshot.Requests,
            Matches + snapshot.Matches,
            NoMatches + snapshot.NoMatches,
            Unavailable + snapshot.Unavailable,
            ProtocolErrors + snapshot.ProtocolErrors,
            Cancellations + snapshot.Cancellations,
            DurationTicks + snapshot.DurationTicks,
            Buckets.Zip(snapshot.Buckets, (left, right) => left + right).ToArray());

    public OpenAiModerationMetricTotals Add(OpenAiModerationMetricSnapshotValues values) =>
        new(
            Requests + values.Requests,
            Matches + values.Matches,
            NoMatches + values.NoMatches,
            Unavailable + values.Unavailable,
            ProtocolErrors + values.ProtocolErrors,
            Cancellations + values.Cancellations,
            DurationTicks + values.DurationTicks,
            Buckets.Zip(values.Buckets, (left, right) => left + right).ToArray());
}

public sealed record OpenAiModerationMetricSnapshotValues(
    long Requests,
    long Matches,
    long NoMatches,
    long Unavailable,
    long ProtocolErrors,
    long Cancellations,
    long DurationTicks,
    long[] Buckets);

public sealed record OpenAiModerationMetricBudgetOptions(
    double MaxUnavailableRatio,
    double MaxP95Seconds,
    long MinimumSamples,
    long WindowSeconds = 900)
{
    public static OpenAiModerationMetricBudgetOptions Defaults =>
        new(0.05, 2.5, 20, 900);

    public static OpenAiModerationMetricBudgetOptions FromConfiguration(
        IConfiguration configuration)
    {
        var options = new OpenAiModerationMetricBudgetOptions(
            configuration.GetValue("ContentClassifier:OpenAI:Budget:MaxUnavailableRatio", 0.05),
            configuration.GetValue("ContentClassifier:OpenAI:Budget:MaxP95Seconds", 2.5),
            configuration.GetValue("ContentClassifier:OpenAI:Budget:MinimumSamples", 20L),
            configuration.GetValue("ContentClassifier:OpenAI:Budget:WindowSeconds", 900L));
        if (!double.IsFinite(options.MaxUnavailableRatio)
            || options.MaxUnavailableRatio is < 0 or > 1)
            throw new InvalidOperationException(
                "ContentClassifier:OpenAI:Budget:MaxUnavailableRatio must be between 0 and 1");
        if (!double.IsFinite(options.MaxP95Seconds)
            || options.MaxP95Seconds is <= 0 or > 60)
            throw new InvalidOperationException(
                "ContentClassifier:OpenAI:Budget:MaxP95Seconds must be greater than 0 and at most 60");
        if (options.MinimumSamples is < 1 or > 100_000)
            throw new InvalidOperationException(
                "ContentClassifier:OpenAI:Budget:MinimumSamples must be between 1 and 100000");
        if (options.WindowSeconds is < 60 or > 86_400)
            throw new InvalidOperationException(
                "ContentClassifier:OpenAI:Budget:WindowSeconds must be between 60 and 86400");
        return options;
    }
}

public sealed record OpenAiModerationMetricBudgetEvaluation(
    long EvaluatedSamples,
    double UnavailableRatio,
    double P95Seconds,
    bool UnavailableBreached,
    bool P95Breached)
{
    public bool AnyBreached => UnavailableBreached || P95Breached;
}

public static class OpenAiModerationMetricCalculator
{
    private static readonly double[] BucketsSeconds =
        [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5];

    public static OpenAiModerationMetricBudgetEvaluation Evaluate(
        OpenAiModerationMetricTotals totals,
        OpenAiModerationMetricBudgetOptions options)
    {
        var evaluated = Math.Max(0, totals.Requests - totals.Cancellations);
        var ratio = evaluated == 0 ? 0 : (double)totals.Unavailable / evaluated;
        var p95 = EstimateP95(totals.Buckets, evaluated);
        var eligible = evaluated >= options.MinimumSamples;
        return new(evaluated, ratio, p95,
            eligible && ratio > options.MaxUnavailableRatio,
            eligible && p95 > options.MaxP95Seconds);
    }

    public static double EstimateP95(long[] buckets, long count)
    {
        if (count <= 0) return 0;
        var target = Math.Max(1, (long)Math.Ceiling(count * 0.95));
        var cumulative = 0L;
        for (var index = 0; index < buckets.Length; index++)
        {
            cumulative += buckets[index];
            if (cumulative >= target)
                return index < BucketsSeconds.Length ? BucketsSeconds[index] : double.PositiveInfinity;
        }
        return double.PositiveInfinity;
    }
}

public sealed class OpenAiModerationMetricStore(NpgsqlDataSource dataSource)
{
    public async Task AppendAsync(OpenAiModerationMetricSnapshot snapshot,
        CancellationToken ct = default)
    {
        Validate(snapshot);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO content_classifier_metric_snapshots(
                instance_id, sequence, classifier, requests, matches, no_matches,
                unavailable, protocol_errors, cancellations, duration_ticks,
                bucket_0, bucket_1, bucket_2, bucket_3, bucket_4,
                bucket_5, bucket_6, bucket_7, bucket_8, bucket_9)
            VALUES ($1, $2, 'openai', $3, $4, $5, $6, $7, $8, $9,
                    $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
            ON CONFLICT (instance_id, sequence) DO NOTHING
            """);
        command.Parameters.AddWithValue(snapshot.InstanceId);
        command.Parameters.AddWithValue(snapshot.Sequence);
        command.Parameters.AddWithValue(snapshot.Requests);
        command.Parameters.AddWithValue(snapshot.Matches);
        command.Parameters.AddWithValue(snapshot.NoMatches);
        command.Parameters.AddWithValue(snapshot.Unavailable);
        command.Parameters.AddWithValue(snapshot.ProtocolErrors);
        command.Parameters.AddWithValue(snapshot.Cancellations);
        command.Parameters.AddWithValue(snapshot.DurationTicks);
        foreach (var bucket in snapshot.Buckets)
            command.Parameters.AddWithValue(bucket);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendAndEvaluateAsync(
        OpenAiModerationMetricSnapshot snapshot,
        OpenAiModerationMetricBudgetOptions options,
        CancellationToken ct = default)
    {
        Validate(snapshot);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO content_classifier_metric_snapshots(
                instance_id, sequence, classifier, requests, matches, no_matches,
                unavailable, protocol_errors, cancellations, duration_ticks,
                bucket_0, bucket_1, bucket_2, bucket_3, bucket_4,
                bucket_5, bucket_6, bucket_7, bucket_8, bucket_9)
            VALUES ($1, $2, 'openai', $3, $4, $5, $6, $7, $8, $9,
                    $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
            ON CONFLICT (instance_id, sequence) DO NOTHING
            """, connection, transaction))
        {
            AddSnapshotParameters(insert, snapshot);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var totals = await ReadTotalsAsync(connection, transaction,
            options.WindowSeconds, ct);
        await EvaluateBudgetAsync(connection, transaction, totals, options, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task EvaluateCurrentBudgetAsync(
        OpenAiModerationMetricBudgetOptions options,
        CancellationToken ct = default)
    {
        ValidateWindow(options.WindowSeconds);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var totals = await ReadTotalsAsync(connection, transaction,
            options.WindowSeconds, ct);
        await EvaluateBudgetAsync(connection, transaction, totals, options, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<OpenAiModerationMetricTotals> ReadTotalsAsync(
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                COALESCE(sum(requests), 0), COALESCE(sum(matches), 0),
                COALESCE(sum(no_matches), 0), COALESCE(sum(unavailable), 0),
                COALESCE(sum(protocol_errors), 0), COALESCE(sum(cancellations), 0),
                COALESCE(sum(duration_ticks), 0),
                COALESCE(sum(bucket_0), 0), COALESCE(sum(bucket_1), 0),
                COALESCE(sum(bucket_2), 0), COALESCE(sum(bucket_3), 0),
                COALESCE(sum(bucket_4), 0), COALESCE(sum(bucket_5), 0),
                COALESCE(sum(bucket_6), 0), COALESCE(sum(bucket_7), 0),
                COALESCE(sum(bucket_8), 0), COALESCE(sum(bucket_9), 0)
            FROM content_classifier_metric_snapshots
            WHERE classifier = 'openai'
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), Enumerable.Range(7, 10)
                .Select(reader.GetInt64).ToArray());
    }

    public async Task<OpenAiModerationMetricTotals> ReadWindowTotalsAsync(
        long windowSeconds, CancellationToken ct = default)
    {
        ValidateWindow(windowSeconds);
        await using var command = dataSource.CreateCommand("""
            SELECT
                COALESCE(sum(requests), 0), COALESCE(sum(matches), 0),
                COALESCE(sum(no_matches), 0), COALESCE(sum(unavailable), 0),
                COALESCE(sum(protocol_errors), 0), COALESCE(sum(cancellations), 0),
                COALESCE(sum(duration_ticks), 0),
                COALESCE(sum(bucket_0), 0), COALESCE(sum(bucket_1), 0),
                COALESCE(sum(bucket_2), 0), COALESCE(sum(bucket_3), 0),
                COALESCE(sum(bucket_4), 0), COALESCE(sum(bucket_5), 0),
                COALESCE(sum(bucket_6), 0), COALESCE(sum(bucket_7), 0),
                COALESCE(sum(bucket_8), 0), COALESCE(sum(bucket_9), 0)
            FROM content_classifier_metric_snapshots
            WHERE classifier = 'openai'
              AND captured_at >= now() - ($1::bigint * interval '1 second')
            """);
        command.Parameters.AddWithValue(windowSeconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), Enumerable.Range(7, 10)
                .Select(reader.GetInt64).ToArray());
    }

    private static async Task<OpenAiModerationMetricTotals> ReadTotalsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long windowSeconds, CancellationToken ct)
    {
        ValidateWindow(windowSeconds);
        await using var command = new NpgsqlCommand("""
            SELECT
                COALESCE(sum(requests), 0), COALESCE(sum(matches), 0),
                COALESCE(sum(no_matches), 0), COALESCE(sum(unavailable), 0),
                COALESCE(sum(protocol_errors), 0), COALESCE(sum(cancellations), 0),
                COALESCE(sum(duration_ticks), 0),
                COALESCE(sum(bucket_0), 0), COALESCE(sum(bucket_1), 0),
                COALESCE(sum(bucket_2), 0), COALESCE(sum(bucket_3), 0),
                COALESCE(sum(bucket_4), 0), COALESCE(sum(bucket_5), 0),
                COALESCE(sum(bucket_6), 0), COALESCE(sum(bucket_7), 0),
                COALESCE(sum(bucket_8), 0), COALESCE(sum(bucket_9), 0)
            FROM content_classifier_metric_snapshots
            WHERE classifier = 'openai'
              AND captured_at >= now() - ($1::bigint * interval '1 second')
            """, connection, transaction);
        command.Parameters.AddWithValue(windowSeconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), Enumerable.Range(7, 10)
                .Select(reader.GetInt64).ToArray());
    }

    private static async Task EvaluateBudgetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        OpenAiModerationMetricTotals totals,
        OpenAiModerationMetricBudgetOptions options,
        CancellationToken ct)
    {
        var evaluation = OpenAiModerationMetricCalculator.Evaluate(totals, options);
        await UpsertBudgetAlertAsync(connection, transaction,
            "openai:unavailable_ratio", "unavailable_ratio",
            evaluation.UnavailableBreached, evaluation.UnavailableRatio,
            options.MaxUnavailableRatio, evaluation.EvaluatedSamples, ct);
        await UpsertBudgetAlertAsync(connection, transaction,
            "openai:p95_latency", "p95_latency",
            evaluation.P95Breached, evaluation.P95Seconds,
            options.MaxP95Seconds, evaluation.EvaluatedSamples, ct);
    }

    private static async Task UpsertBudgetAlertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string eventKey, string budgetKind, bool breached,
        double observed, double threshold, long samples, CancellationToken ct)
    {
        if (breached)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO content_classifier_budget_alerts(
                    event_key, classifier, budget_kind, status, observed_value,
                    threshold_value, sample_count)
                VALUES ($1, 'openai', $2, 'open', $3, $4, $5)
                ON CONFLICT (event_key) DO UPDATE SET
                    status = 'open', observed_value = EXCLUDED.observed_value,
                    threshold_value = EXCLUDED.threshold_value,
                    sample_count = EXCLUDED.sample_count,
                    last_seen_at = now(), resolved_at = NULL
                """, connection, transaction);
            command.Parameters.AddWithValue(eventKey);
            command.Parameters.AddWithValue(budgetKind);
            AddNumeric(command, observed);
            AddNumeric(command, threshold);
            command.Parameters.AddWithValue(samples);
            await command.ExecuteNonQueryAsync(ct);
            return;
        }

        await using var resolve = new NpgsqlCommand("""
            UPDATE content_classifier_budget_alerts
            SET status = 'resolved', resolved_at = COALESCE(resolved_at, now()),
                last_seen_at = now(), observed_value = $2,
                threshold_value = $3, sample_count = $4
            WHERE event_key = $1 AND status = 'open'
            """, connection, transaction);
        resolve.Parameters.AddWithValue(eventKey);
        AddNumeric(resolve, observed);
        AddNumeric(resolve, threshold);
        resolve.Parameters.AddWithValue(samples);
        await resolve.ExecuteNonQueryAsync(ct);
    }

    private static void AddSnapshotParameters(NpgsqlCommand command,
        OpenAiModerationMetricSnapshot snapshot)
    {
        command.Parameters.AddWithValue(snapshot.InstanceId);
        command.Parameters.AddWithValue(snapshot.Sequence);
        command.Parameters.AddWithValue(snapshot.Requests);
        command.Parameters.AddWithValue(snapshot.Matches);
        command.Parameters.AddWithValue(snapshot.NoMatches);
        command.Parameters.AddWithValue(snapshot.Unavailable);
        command.Parameters.AddWithValue(snapshot.ProtocolErrors);
        command.Parameters.AddWithValue(snapshot.Cancellations);
        command.Parameters.AddWithValue(snapshot.DurationTicks);
        foreach (var bucket in snapshot.Buckets)
            command.Parameters.AddWithValue(bucket);
    }

    private static void AddNumeric(NpgsqlCommand command, double value)
    {
        var parameter = command.Parameters.Add("", NpgsqlDbType.Numeric);
        parameter.Value = double.IsFinite(value)
            ? Convert.ToDecimal(value)
            : 0m;
    }

    private static void ValidateWindow(long windowSeconds)
    {
        if (windowSeconds is < 60 or > 86_400)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
    }

    private static void Validate(OpenAiModerationMetricSnapshot snapshot)
    {
        if (snapshot.InstanceId == Guid.Empty || snapshot.Sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        if (snapshot.Buckets is not { Length: 10 }
            || snapshot.Requests < 0 || snapshot.Matches < 0
            || snapshot.NoMatches < 0 || snapshot.Unavailable < 0
            || snapshot.ProtocolErrors < 0 || snapshot.Cancellations < 0
            || snapshot.DurationTicks < 0 || snapshot.Buckets.Any(value => value < 0))
            throw new ArgumentOutOfRangeException(nameof(snapshot));
    }
}

public sealed class OpenAiModerationMetricFlushService(
    OpenAiModerationMetrics metrics,
    OpenAiModerationMetricStore store,
    OpenAiModerationMetricBudgetOptions budgetOptions,
    ILogger<OpenAiModerationMetricFlushService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private readonly Guid _instanceId = Guid.NewGuid();
    private long _nextSequence = 1;
    private OpenAiModerationMetricSnapshot? _pending;

    public Guid InstanceId => _instanceId;

    public Task FlushOnceAsync(CancellationToken ct = default) => FlushAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            if (_pending is null)
            {
                var candidate = metrics.Capture(_instanceId, _nextSequence);
                if (candidate.Requests == 0 && candidate.Cancellations == 0)
                {
                    await store.EvaluateCurrentBudgetAsync(budgetOptions, ct);
                    return;
                }
                _pending = candidate;
            }
            await store.AppendAndEvaluateAsync(_pending, budgetOptions, ct);
            metrics.Acknowledge(_pending);
            _pending = null;
            _nextSequence++;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "OpenAI moderation metric snapshot flush failed");
        }
    }
}
