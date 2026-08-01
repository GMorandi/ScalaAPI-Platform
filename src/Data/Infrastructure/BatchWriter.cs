using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Sub2Api.Data.Infrastructure;

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
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _worker = Task.Run(ProcessLoopAsync);
    }

    public bool Enqueue(T item) => _channel.Writer.TryWrite(item);

    private async Task ProcessLoopAsync()
    {
        var batch = new List<T>(_batchSize);
        var reader = _channel.Reader;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                batch.Clear();

                if (await reader.WaitToReadAsync(_cts.Token))
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
                _logger.LogError(ex, "BatchWriter flush error, dropping {Count} items", batch.Count);
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
        try
        {
            await _db.Insertable(batch).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BatchWriter insert failed ({Count} rows), retrying once", batch.Count);
            try
            {
                await Task.Delay(100);
                await _db.Insertable(batch).ExecuteCommandAsync();
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "BatchWriter retry failed, dropping {Count} rows", batch.Count);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { }
        _cts.Dispose();
    }
}
