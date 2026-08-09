using System.Security.Claims;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;

namespace ScalaAPI.Admin.Endpoints;

public static class AnnouncementEndpoints
{
    public static void MapAnnouncementEndpoints(this WebApplication app)
    {
        var user = app.MapGroup("/user/announcements").RequireAuthorization("UserOnly");
        user.MapGet("/", async (
            ClaimsPrincipal principal,
            AnnouncementStore announcements,
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
                return Results.BadRequest(new { error = "invalid_announcement_limit" });
            }
        });

        user.MapPost("/{announcementId:long}/read", async (
            long announcementId,
            ClaimsPrincipal principal,
            AnnouncementStore announcements,
            HttpRequest request,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            try
            {
                var result = await announcements.MarkReadAsync(userId, announcementId,
                    request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
                return result is null
                    ? Results.NotFound()
                    : Results.Ok(new { read_at = result.ReadAt, duplicate = !result.Created });
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "invalid_announcement_id" });
            }
        });
    }
}
