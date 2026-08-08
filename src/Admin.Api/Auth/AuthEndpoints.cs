namespace ScalaAPI.Admin.Auth;

using System.Security.Claims;
using SqlSugar;
using ScalaAPI.Data.Entities;

public record LoginRequest(string Username, string Password);
public record LoginResponse(
    string Token, string Username, string RefreshToken, DateTime ExpiresAt, string SessionId);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/auth/login", async (LoginRequest req, ISqlSugarClient db,
            AuthSessionService sessions, HttpContext http) =>
        {
            var normalized = req.Username.Trim().ToLowerInvariant();
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == normalized).FirstAsync();
            if (account is null || account.Status != "active" || account.Role != "admin"
                || account.PasswordHash is null
                || !BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
                return Results.Unauthorized();

            account.LastLoginAt = DateTime.UtcNow;
            await db.Updateable(account).UpdateColumns(x => x.LastLoginAt).ExecuteCommandAsync();
            var tokens = await sessions.IssueAsync(account.Id, account.Email, account.Role,
                http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent);
            return Results.Ok(new LoginResponse(tokens.Token, account.Email,
                tokens.RefreshToken, tokens.ExpiresAt, tokens.SessionId));
        }).AllowAnonymous();

        app.MapPost("/admin/auth/logout", async (ClaimsPrincipal principal,
            AuthSessionService sessions) =>
        {
            var subject = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            var sessionId = principal.FindFirst("sid")?.Value;
            if (!long.TryParse(subject, out var userId) || string.IsNullOrWhiteSpace(sessionId))
                return Results.Unauthorized();
            await sessions.RevokeAsync(userId, sessionId);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");
    }
}
