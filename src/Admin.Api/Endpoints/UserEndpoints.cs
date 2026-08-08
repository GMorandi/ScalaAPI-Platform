using System.Security.Claims;
using Orleans;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Admin.Models;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/users").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IClusterClient client, ListingRepository repo, int page = 0, int size = 20) =>
        {
            var ids = await repo.GetIntegerGrainIds("user", page, size);
            var total = await repo.CountGrains("user");
            var items = new List<UserProjection>();
            foreach (var id in ids)
            {
                var grain = client.GetGrain<IUserGrain>(id);
                items.Add(await grain.GetAuthProjection());
            }
            return Results.Ok(new PagedResponse<UserProjection>(items, total, page, size));
        });

        group.MapGet("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            return Results.Ok(await grain.GetAuthProjection());
        });

        group.MapPost("/", async (UserCreateRequest req, IClusterClient client, ListingRepository repo) =>
        {
            var allocator = client.GetGrain<IIdAllocatorGrain>("user");
            var id = await allocator.Next();
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.Create(new UserCreate(
                req.Role, 0m, req.Concurrency, req.RpmLimit, req.AllowedGroups));
            await repo.RegisterInteger("user", id);
            return Results.Created($"/admin/users/{id}", new { id });
        });

        group.MapPut("/{id:long}", async (long id, UserCreateRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.Update(new UserConfiguration(
                req.Role, req.Concurrency, req.RpmLimit, req.AllowedGroups));
            return Results.NoContent();
        });

        group.MapPatch("/{id:long}/status", async (long id, StatusRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.SetStatus(req.Status);
            return Results.NoContent();
        });

        group.MapPost("/{id:long}/balance", async (
            long id,
            BalanceRequest req,
            ClaimsPrincipal principal,
            HttpRequest http,
            IClusterClient client,
            BalanceAdjustmentStore store,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();

            var idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(idempotencyKey) || idempotencyKey.Length > 128)
                return Results.BadRequest(new { error = "A 1-128 character Idempotency-Key is required" });
            if (req.Delta == 0m || decimal.Round(req.Delta, 8) != req.Delta
                || req.Delta is < -1_000_000m or > 1_000_000m)
                return Results.BadRequest(new { error = "Delta must be non-zero, within 1,000,000, and have at most 8 decimals" });
            var reason = req.Reason?.Trim() ?? "";
            if (reason.Length is < 3 or > 500)
                return Results.BadRequest(new { error = "Reason must contain 3-500 characters" });

            var adjustment = await store.RecordAsync(
                id, actorId, idempotencyKey, req.Delta, reason, ct);
            if (adjustment.Status == BalanceAdjustmentStatus.UserNotFound)
                return Results.NotFound(new { error = "User not found" });
            if (adjustment.Status == BalanceAdjustmentStatus.Conflict)
                return Results.Conflict(new
                {
                    error = "Idempotency-Key was already used with different adjustment data",
                    effect_id = adjustment.EffectId,
                });
            if (adjustment.Status == BalanceAdjustmentStatus.InsufficientFunds)
                return Results.Conflict(new
                {
                    error = "Adjustment would reduce available balance below active holds",
                    balance = adjustment.BalanceAfter,
                });

            try
            {
                await client.GetGrain<IUserGrain>(id).ApplyBalanceSnapshot(
                    adjustment.EffectId, adjustment.BalanceAfter);
            }
            catch
            {
                return Results.Json(new
                {
                    error = "Balance effect was committed but its runtime projection is unavailable",
                    effect_id = adjustment.EffectId,
                    retryable = true,
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                effect_id = adjustment.EffectId,
                ledger_id = adjustment.LedgerId,
                balance = adjustment.BalanceAfter,
                duplicate = adjustment.Status == BalanceAdjustmentStatus.Replay,
            });
        });

        group.MapDelete("/{id:long}", async (long id, IClusterClient client, ListingRepository repo) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.Delete();
            await repo.Unregister("user", id.ToString());
            return Results.NoContent();
        });
    }
}
