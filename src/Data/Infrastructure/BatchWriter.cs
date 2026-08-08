using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace ScalaAPI.Data.Infrastructure;

public class BatchWriter<T> : IAsyncDisposable where T : class
{
    private readonly Channel<T> _channel;
    private readonly ISqlSugarClient _db;
    private readonly ILogger _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _window;
    private readonly Task _worker;
    private readonly CancellationTokenSource _cts = new();

    public BatchWriter(ISqlSugarClient db, ILogger logger,
                       int capacity = 4096, int batchSize = 64, int windowMs = 3)
    {
        _db = db;
        _logger = logger;
        _batchSize = batchSize;
        _window = TimeSpan.FromMilliseconds(windowMs);
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _worker = Task.Run(ProcessLoopAsync);
    }

    public bool Enqueue(T item)
    {
        try
        {
            _channel.Writer.WriteAsync(item, _cts.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ProcessLoopAsync()
    {
        var batch = new List<T>(_batchSize);
        var reader = _channel.Reader;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                batch.Clear();

                if (!await reader.WaitToReadAsync(_cts.Token)) break;
                else
                {
                    while (batch.Count < _batchSize && reader.TryRead(out var item))
                        batch.Add(item);

                    if (batch.Count < _batchSize)
                    {
                        using var delay = new CancellationTokenSource(_window);
                        try
                        {
                            while (batch.Count < _batchSize &&
                                   await reader.WaitToReadAsync(delay.Token))
                            {
                                while (batch.Count < _batchSize && reader.TryRead(out var item))
                                    batch.Add(item);
                            }
                        }
                        catch (OperationCanceledException) { }
                    }

                    if (batch.Count > 0)
                        await FlushBatch(batch);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BatchWriter flush loop failed with {Count} pending items", batch.Count);
            }
        }

        while (reader.TryRead(out var remaining))
            batch.Add(remaining);
        if (batch.Count > 0)
        {
            try { await FlushBatch(batch); }
            catch (Exception ex) { _logger.LogError(ex, "BatchWriter final flush error"); }
        }
    }

    private async Task FlushBatch(List<T> batch)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await _db.Insertable(batch).ExecuteCommandAsync();
                return;
            }
            catch (Exception ex) when (!_cts.Token.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 100 * (1 << Math.Min(attempt++, 8))));
                _logger.LogWarning(ex,
                    "BatchWriter insert failed ({Count} rows), retrying in {Delay}",
                    batch.Count, delay);
                await Task.Delay(delay, _cts.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { _cts.Cancel(); }
        _cts.Dispose();
    }
}
