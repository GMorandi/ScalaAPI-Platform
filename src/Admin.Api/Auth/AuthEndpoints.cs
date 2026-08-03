namespace Sub2Api.Admin.Auth;

using SqlSugar;
using Sub2Api.Data.Entities;

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/auth/login", async (LoginRequest req, ISqlSugarClient db, JwtService jwt) =>
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
            var token = jwt.GenerateToken(account.Email, account.Role, account.Id);
            return Results.Ok(new LoginResponse(token, account.Email));
        }).AllowAnonymous();
    }
}
