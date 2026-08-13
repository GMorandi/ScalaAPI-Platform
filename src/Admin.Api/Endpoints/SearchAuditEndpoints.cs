using ScalaAPI.Data.Search;
using Npgsql;

namespace ScalaAPI.Admin.Endpoints;

public static class SearchAuditEndpoints
{
    public static void MapSearchAuditEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/search")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/audit", async (ISearchHistoryStore store,
            string? provider_platform, string? status, int limit = 50) =>
        {
            var entries = await store.ListForAuditAsync(provider_platform, status, limit);
            return Results.Ok(new
            {
                items = entries.Select(e => new
                {
                    e.Id, e.UserId, e.ApiKeyId, e.LeaseId, e.Query,
                    e.DomainFilter, e.RecencyFilter, e.ResultCount, e.Truncated,
                    e.ProviderPlatform, e.ProviderAccountId, e.Status, e.ErrorCode,
                    e.CreatedAt,
                }),
                count = entries.Count,
            });
        });

        group.MapGet("/history", async (ISearchHistoryStore store,
            long user_id, string? since, int limit = 50) =>
        {
            DateTimeOffset? sinceDt = null;
            if (!string.IsNullOrWhiteSpace(since) && DateTimeOffset.TryParse(since, out var parsed))
                sinceDt = parsed;
            var entries = await store.ListByUserAsync(user_id, sinceDt, limit);
            return Results.Ok(new
            {
                items = entries.Select(e => new
                {
                    e.Id, e.LeaseId, e.Query, e.DomainFilter, e.RecencyFilter,
                    e.ResultCount, e.Truncated, e.ProviderPlatform, e.Status,
                    e.ErrorCode, e.CreatedAt,
                }),
                count = entries.Count,
            });
        });

        group.MapGet("/provider-status", async (NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            await using var command = dataSource.CreateCommand("""
                SELECT provider_platform,
                       count(*) AS total_queries,
                       count(*) FILTER (WHERE status = 'error') AS error_count,
                       COALESCE(avg(result_count), 0) AS avg_results
                FROM search_history
                GROUP BY provider_platform
                ORDER BY provider_platform
                """);
            var providers = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var total = reader.GetInt64(1);
                var errors = reader.GetInt64(2);
                providers.Add(new
                {
                    provider_platform = reader.GetString(0),
                    total_queries = total,
                    error_count = errors,
                    error_rate = total > 0 ? Math.Round((double)errors / total, 4) : 0.0,
                    avg_results = Math.Round(reader.GetDouble(3), 2),
                });
            }
            return Results.Ok(new { providers });
        });
    }
}
