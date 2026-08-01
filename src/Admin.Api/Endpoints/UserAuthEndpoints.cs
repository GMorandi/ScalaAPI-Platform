using System.Security.Claims;
using System.Security.Cryptography;
using OtpNet;
using SqlSugar;
using Sub2Api.Admin.Auth;
using Sub2Api.Data.Entities;

namespace Sub2Api.Admin.Endpoints;

public record RegisterRequest(string Email, string Password, string? DisplayName);
public record UserLoginRequest(string Email, string Password, string? TotpCode);
public record OAuthCallbackRequest(string Provider, string Code, string RedirectUri);
public record TotpSetupResponse(string Secret, string QrUri);
public record TotpVerifyRequest(string Code);

public static class UserAuthEndpoints
{
    public static void MapUserAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").AllowAnonymous();
        var user = app.MapGroup("/user").RequireAuthorization();

        auth.MapPost("/register", async (RegisterRequest req, ISqlSugarClient db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Email and password required" });

            var existing = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == req.Email).FirstAsync();
            if (existing is not null)
                return Results.Conflict(new { error = "Email already registered" });

            var account = new UserAccountEntity
            {
                Email = req.Email.ToLowerInvariant(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                DisplayName = req.DisplayName,
                CreatedAt = DateTime.UtcNow,
            };
            await db.Insertable(account).ExecuteCommandAsync();

            return Results.Ok(new { id = account.Id, email = account.Email });
        });

        auth.MapPost("/login", async (UserLoginRequest req, ISqlSugarClient db, JwtService jwt) =>
        {
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == req.Email.ToLowerInvariant()).FirstAsync();
            if (account is null || account.PasswordHash is null)
                return Results.Unauthorized();

            if (!BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
                return Results.Unauthorized();

            if (account.TotpEnabled)
            {
                if (string.IsNullOrEmpty(req.TotpCode))
                    return Results.Json(new { error = "totp_required" }, statusCode: 403);

                if (!VerifyTotp(account, req.TotpCode))
                    return Results.Unauthorized();
            }

            account.LastLoginAt = DateTime.UtcNow;
            await db.Updateable(account).UpdateColumns(x => x.LastLoginAt).ExecuteCommandAsync();

            var token = jwt.GenerateToken(account.Email);
            return Results.Ok(new { token, email = account.Email, role = account.Role });
        });

        auth.MapPost("/oauth/callback", async (OAuthCallbackRequest req, ISqlSugarClient db,
            JwtService jwt, IConfiguration config, IHttpClientFactory httpFactory) =>
        {
            var (email, oauthId) = await ExchangeOAuthCode(req, config, httpFactory);
            if (email is null)
                return Results.BadRequest(new { error = "OAuth exchange failed" });

            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.OAuthProvider == req.Provider && x.OAuthId == oauthId).FirstAsync();

            if (account is null)
            {
                account = await db.Queryable<UserAccountEntity>()
                    .Where(x => x.Email == email).FirstAsync();

                if (account is null)
                {
                    account = new UserAccountEntity
                    {
                        Email = email,
                        OAuthProvider = req.Provider,
                        OAuthId = oauthId,
                        CreatedAt = DateTime.UtcNow,
                    };
                    await db.Insertable(account).ExecuteCommandAsync();
                }
                else
                {
                    account.OAuthProvider = req.Provider;
                    account.OAuthId = oauthId;
                    await db.Updateable(account)
                        .UpdateColumns(x => new { x.OAuthProvider, x.OAuthId }).ExecuteCommandAsync();
                }
            }

            account.LastLoginAt = DateTime.UtcNow;
            await db.Updateable(account).UpdateColumns(x => x.LastLoginAt).ExecuteCommandAsync();

            var token = jwt.GenerateToken(account.Email);
            return Results.Ok(new { token, email = account.Email, role = account.Role });
        });

        user.MapPost("/totp/setup", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null) return Results.NotFound();

            var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
            account.TotpSecret = secret;
            await db.Updateable(account).UpdateColumns(x => x.TotpSecret).ExecuteCommandAsync();

            var qrUri = $"otpauth://totp/Sub2Api:{email}?secret={secret}&issuer=Sub2Api";
            return Results.Ok(new TotpSetupResponse(secret, qrUri));
        });

        user.MapPost("/totp/verify", async (ClaimsPrincipal principal, TotpVerifyRequest req,
            ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null || account.TotpSecret is null) return Results.NotFound();

            if (!VerifyTotp(account, req.Code))
                return Results.BadRequest(new { error = "Invalid TOTP code" });

            var backupCodes = GenerateBackupCodes();
            account.TotpEnabled = true;
            account.TotpBackupCodes = string.Join(",", backupCodes);
            await db.Updateable(account)
                .UpdateColumns(x => new { x.TotpEnabled, x.TotpBackupCodes }).ExecuteCommandAsync();

            return Results.Ok(new { backup_codes = backupCodes });
        });

        user.MapPost("/totp/disable", async (ClaimsPrincipal principal, TotpVerifyRequest req,
            ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null) return Results.NotFound();

            if (!VerifyTotp(account, req.Code))
                return Results.BadRequest(new { error = "Invalid TOTP code" });

            account.TotpEnabled = false;
            account.TotpSecret = null;
            account.TotpBackupCodes = null;
            await db.Updateable(account)
                .UpdateColumns(x => new { x.TotpEnabled, x.TotpSecret, x.TotpBackupCodes })
                .ExecuteCommandAsync();

            return Results.Ok(new { message = "2FA disabled" });
        });
    }

    private static bool VerifyTotp(UserAccountEntity account, string code)
    {
        if (account.TotpSecret is null) return false;

        var secretBytes = Base32Encoding.ToBytes(account.TotpSecret);
        var totp = new Totp(secretBytes);

        if (totp.VerifyTotp(code, out _, new VerificationWindow(0, 0)))
            return true;

        if (account.TotpBackupCodes is not null)
        {
            var codes = account.TotpBackupCodes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (codes.Contains(code))
            {
                var remaining = codes.Where(c => c != code).ToArray();
                account.TotpBackupCodes = string.Join(",", remaining);
                return true;
            }
        }

        return false;
    }

    private static string[] GenerateBackupCodes()
    {
        var codes = new string[10];
        for (int i = 0; i < 10; i++)
            codes[i] = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        return codes;
    }

    private static async Task<(string? Email, string? OAuthId)> ExchangeOAuthCode(
        OAuthCallbackRequest req, IConfiguration config, IHttpClientFactory httpFactory)
    {
        var client = httpFactory.CreateClient();

        if (req.Provider == "github")
        {
            var clientId = config["OAuth:GitHub:ClientId"];
            var clientSecret = config["OAuth:GitHub:ClientSecret"];

            var tokenResp = await client.PostAsJsonAsync(
                "https://github.com/login/oauth/access_token",
                new { client_id = clientId, client_secret = clientSecret, code = req.Code });
            var tokenData = await tokenResp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (tokenData is null || !tokenData.TryGetValue("access_token", out var accessToken))
                return (null, null);

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sub2Api");
            var userResp = await client.GetAsync("https://api.github.com/user/emails");
            var emails = await userResp.Content.ReadFromJsonAsync<List<GitHubEmail>>();
            var primary = emails?.FirstOrDefault(e => e.Primary)?.Email;
            var userResp2 = await client.GetAsync("https://api.github.com/user");
            var userData = await userResp2.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            var id = userData?.GetValueOrDefault("id")?.ToString();

            return (primary, id);
        }

        if (req.Provider == "google")
        {
            var clientId = config["OAuth:Google:ClientId"];
            var clientSecret = config["OAuth:Google:ClientSecret"];

            var tokenResp = await client.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId!,
                    ["client_secret"] = clientSecret!,
                    ["code"] = req.Code,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = req.RedirectUri,
                }));
            var tokenData = await tokenResp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (tokenData is null || !tokenData.TryGetValue("access_token", out var accessToken))
                return (null, null);

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var userResp = await client.GetAsync("https://www.googleapis.com/oauth2/v2/userinfo");
            var userData = await userResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            var email = userData?.GetValueOrDefault("email")?.ToString();
            var id = userData?.GetValueOrDefault("id")?.ToString();

            return (email, id);
        }

        return (null, null);
    }

    private record GitHubEmail(string Email, bool Primary, bool Verified);
}
