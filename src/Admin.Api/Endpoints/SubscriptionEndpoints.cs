using System.Security.Claims;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Data.Announcements;
using ScalaAPI.Data.Redemptions;
using ScalaAPI.Data.Referrals;
using ScalaAPI.Data.Subscriptions;

namespace ScalaAPI.Admin.Endpoints;

/// <summary>
/// User-facing endpoints for subscription lifecycle, redemption, referrals, and announcements.
/// All endpoints enforce that users can only access their own data.
/// </summary>
public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/user").RequireAuthorization("UserOnly");

        // GET /user/subscriptions - user's subscriptions
        group.MapGet("/subscriptions", async (
            ClaimsPrincipal principal,
            SubscriptionService subscriptions,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            var items = await subscriptions.ListForUserAsync(userId, ct: ct);
            return Results.Ok(new { items });
        });

        // POST /user/redemptions - redeem a code
        group.MapPost("/redemptions", async (
            ClaimsPrincipal principal,
            RedemptionRequest request,
            RedemptionService redemptions,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Code))
                return Results.BadRequest(new { error = "Code is required" });

            var result = await redemptions.RedeemAsync(request.Code, userId, ct);
            return result.Status switch
            {
                RedemptionStatus.Redeemed => Results.Ok(new
                {
                    status = "redeemed",
                    plan_id = result.PlanId,
                }),
                RedemptionStatus.Duplicate => Results.Conflict(new
                {
                    error = "Code already redeemed by this user",
                }),
                RedemptionStatus.CodeNotFound => Results.NotFound(new
                {
                    error = "Redemption code not found",
                }),
                RedemptionStatus.Expired => Results.BadRequest(new
                {
                    error = "Redemption code has expired",
                }),
                RedemptionStatus.UsageLimitReached => Results.BadRequest(new
                {
                    error = "Redemption code usage limit reached",
                }),
                _ => Results.BadRequest(new { error = "Invalid redemption request" }),
            };
        });

        // GET /user/referrals - user's referral status
        group.MapGet("/referrals", async (
            ClaimsPrincipal principal,
            ReferralService referrals,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            var items = await referrals.ListForReferrerAsync(userId, ct);
            return Results.Ok(new { items });
        });

        // GET /user/announcements - user's announcements (filtered by target)
        group.MapGet("/announcements-lifecycle", async (
            ClaimsPrincipal principal,
            AnnouncementService announcements,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            try
            {
                var items = await announcements.ListForUserAsync(userId, limit, ct);
                return Results.Ok(new { items });
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "invalid_limit" });
            }
        });

        // POST /user/announcements-lifecycle/{id}/read - mark as read
        group.MapPost("/announcements-lifecycle/{announcementId:long}/read", async (
            long announcementId,
            ClaimsPrincipal principal,
            AnnouncementService announcements,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            try
            {
                var result = await announcements.MarkReadAsync(userId, announcementId, ct);
                return result.Status switch
                {
                    ReadStatus.Created => Results.Ok(new
                    {
                        read_at = result.ReadAt,
                        duplicate = false,
                    }),
                    ReadStatus.Duplicate => Results.Ok(new
                    {
                        read_at = result.ReadAt,
                        duplicate = true,
                    }),
                    ReadStatus.NotFound => Results.NotFound(),
                    _ => Results.BadRequest(new { error = "invalid_request" }),
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "invalid_announcement_id" });
            }
        });
    }
}

public record RedemptionRequest(string Code);
