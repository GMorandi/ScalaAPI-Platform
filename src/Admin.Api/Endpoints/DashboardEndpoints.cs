using Sub2Api.Admin.Data;
using Sub2Api.Admin.Models;

namespace Sub2Api.Admin.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/admin/dashboard", async (ListingRepository repo) =>
        {
            var accounts = await repo.CountGrains("account");
            var groups = await repo.CountGrains("group");
            var users = await repo.CountGrains("user");
            var apiKeys = await repo.CountGrains("apiKey");
            return Results.Ok(new DashboardStats(accounts, groups, users, apiKeys));
        }).RequireAuthorization("AdminOnly");
    }
}
