using Npgsql;

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
    ILogger<OpenAiModerationMetricFlushService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private readonly Guid _instanceId = Guid.NewGuid();
    private long _nextSequence = 1;
    private OpenAiModerationMetricSnapshot? _pending;

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
                    return;
                _pending = candidate;
            }
            await store.AppendAsync(_pending, ct);
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
