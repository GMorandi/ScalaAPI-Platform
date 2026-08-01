namespace Sub2Api.Admin.Auth;

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/auth/login", (LoginRequest req, IConfiguration config, JwtService jwt) =>
        {
            var adminUser = config["Admin:Username"];
            var adminPass = config["Admin:Password"];

            if (req.Username != adminUser || req.Password != adminPass)
                return Results.Unauthorized();

            var token = jwt.GenerateToken(req.Username);
            return Results.Ok(new LoginResponse(token, req.Username));
        }).AllowAnonymous();
    }
}
