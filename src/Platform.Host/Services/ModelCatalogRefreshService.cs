using System.Text.Json;
using Npgsql;
using Orleans;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Background service that periodically aggregates supported models from all
/// active accounts and writes a unified OpenAI-format model list to Garnet,
/// so that anonymous GET /v1/models returns a populated catalog.
/// </summary>
public sealed class ModelCatalogRefreshService(
    NpgsqlDataSource dataSource,
    IClusterClient cluster,
    GarnetWriteThroughService garnetWriter,
    IConfiguration configuration,
    ILogger<ModelCatalogRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("ModelCatalog:RefreshSeconds", 60), 15, 3600));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Model catalog refresh failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var accountIds = await GetActiveAccountIdsAsync(ct);
        var models = new HashSet<string>(StringComparer.Ordinal);

        foreach (var accountId in accountIds)
        {
            try
            {
                var grain = cluster.GetGrain<IAccountGrain>(accountId);
                var projection = await grain.GetProjection();

                if (!projection.Schedulable)
                    continue;

                foreach (var model in projection.SupportedModels)
                {
                    if (!string.IsNullOrWhiteSpace(model))
                        models.Add(model);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Model catalog: failed to read account {AccountId}", accountId);
            }
        }

        var catalog = BuildOpenAiModelList(models);
        garnetWriter.WriteModelsList(catalog);

        logger.LogDebug("Model catalog refresh: {ModelCount} models from {AccountCount} accounts",
            models.Count, accountIds.Length);

        return models.Count;
    }

    private async Task<long[]> GetActiveAccountIdsAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand();
        command.CommandText =
            "SELECT entity_id FROM entity_registry WHERE entity_type = 'account' AND status = 'active'";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }

    private static string BuildOpenAiModelList(HashSet<string> models)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("object", "list");
        writer.WriteStartArray("data");

        foreach (var model in models.OrderBy(m => m, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", model);
            writer.WriteString("object", "model");
            writer.WriteNumber("created", now);
            writer.WriteString("owned_by", "system");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
