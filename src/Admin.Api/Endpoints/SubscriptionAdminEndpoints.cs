using ScalaAPI.Data.Announcements;
using ScalaAPI.Data.Redemptions;
using ScalaAPI.Data.Referrals;
using ScalaAPI.Data.Subscriptions;

namespace ScalaAPI.Admin.Endpoints;

/// <summary>
/// Admin endpoints for managing subscription lifecycle, redemption codes,
/// referral attributions, and announcements.
/// </summary>
public static class SubscriptionAdminEndpoints
{
    public static void MapSubscriptionAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("AdminOnly");

        // GET /admin/subscriptions - all subscriptions
        group.MapGet("/subscriptions-lifecycle", async (
            SubscriptionService subscriptions,
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default) =>
        {
            var items = await subscriptions.ListAllAsync(limit, offset, ct);
            return Results.Ok(new { items });
        });

        // POST /admin/redemption-codes - create redemption code
        group.MapPost("/redemption-codes", async (
            RedemptionCodeCreateRequest request,
            RedemptionService redemptions,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlanId) || request.MaxUses < 1)
                return Results.BadRequest(new { error = "PlanId and MaxUses >= 1 are required" });

            var validity = request.ValidityDays.HasValue
                ? TimeSpan.FromDays(request.ValidityDays.Value)
                : (TimeSpan?)null;

            var result = await redemptions.CreateCodeAsync(
                request.PlanId, request.MaxUses, validity, request.PromotionId, ct);

            return result.Status switch
            {
                RedemptionCodeStatus.Created => Results.Created(
                    $"/admin/redemption-codes/{result.CodeId}",
                    new
                    {
                        code_id = result.CodeId,
                        code = result.PlaintextCode,
                    }),
                _ => Results.BadRequest(new { error = "Failed to create redemption code" }),
            };
        });

        // GET /admin/referrals - all referrals
        group.MapGet("/referrals-lifecycle", async (
            ReferralService referrals,
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default) =>
        {
            var items = await referrals.ListAllAsync(limit, offset, ct);
            return Results.Ok(new { items });
        });

        // POST /admin/announcements-lifecycle - create announcement
        group.MapPost("/announcements-lifecycle", async (
            AnnouncementCreateRequest request,
            AnnouncementService announcements,
            CancellationToken ct) =>
        {
            var result = await announcements.CreateAsync(
                request.Title, request.Content, request.TargetAudience,
                request.ScheduledAt, request.ExpiresAt, request.CreatedBy, ct);

            return result.Status switch
            {
                AnnouncementCreationStatus.Created => Results.Created(
                    $"/admin/announcements-lifecycle/{result.AnnouncementId}",
                    new { id = result.AnnouncementId }),
                _ => Results.BadRequest(new { error = "Failed to create announcement" }),
            };
        });
    }
}

public record RedemptionCodeCreateRequest(
    string PlanId,
    int MaxUses = 1,
    int? ValidityDays = null,
    string? PromotionId = null);

public record AnnouncementCreateRequest(
    string Title,
    string Content,
    string TargetAudience = "all",
    DateTime? ScheduledAt = null,
    DateTime? ExpiresAt = null,
    long? CreatedBy = null);
