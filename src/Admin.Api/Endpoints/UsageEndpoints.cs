using ScalaAPI.Data.Repositories;

namespace ScalaAPI.Admin.Endpoints;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/usage")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IUsageLogRepository repo,
            long? userId, string? model, DateTime? from, DateTime? to,
            int page = 1, int size = 20) =>
        {
            if (page < 1) page = 1;
            if (size < 1 || size > 100) size = 20;

            var items = await repo.GetPaged(userId, model, from, to, page, size);
            var total = await repo.Count(userId, model, from, to);

            return Results.Ok(new
            {
                items,
                total,
                page,
                size,
                pages = (int)Math.Ceiling((double)total / size)
            });
        });
    }
}
