using System.Security.Claims;
using System.Security.Cryptography;
using OtpNet;
using SqlSugar;
using Orleans;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Data.Entities;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public record RegisterRequest(string Email, string Password, string? DisplayName);
public record UserLoginRequest(string Email, string Password, string? TotpCode);
public record RefreshRequest(string RefreshToken);
public record OAuthCallbackRequest(string Provider, string Code, string RedirectUri);
public record TotpSetupResponse(string Secret, string QrUri);
public record TotpVerifyRequest(string Code);
public record PasswordResetRequest(string Email);
public record PasswordResetConfirmRequest(string Token, string NewPassword);

public static class UserAuthEndpoints
{
    public static void MapUserAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").AllowAnonymous();
        var user = app.MapGroup("/user").RequireAuthorization("UserOnly");

        auth.MapPost("/register", async (RegisterRequest req, ISqlSugarClient db, IClusterClient client, ListingRepository registry) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Email and password required" });
            if (req.Password.Length < 12)
                return Results.BadRequest(new { error = "Password must be at least 12 characters" });

            var email = req.Email.Trim().ToLowerInvariant();
            var existing = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (existing is not null)
                return Results.Conflict(new { error = "Email already registered" });

            var account = new UserAccountEntity
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                DisplayName = req.DisplayName,
                CreatedAt = DateTime.UtcNow,
            };
            await db.Insertable(account).ExecuteCommandAsync();
            account.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
                "SELECT id FROM user_accounts WHERE email = @email",
                new SugarParameter("@email", email)));
            await client.GetGrain<IUserGrain>(account.Id).Create(new UserUpsert(
                "user", 0, 1, 0, []));
            await registry.RegisterInteger("user", account.Id);

            return Results.Ok(new { id = account.Id, email = account.Email });
        });

        auth.MapPost("/login", async (UserLoginRequest req, ISqlSugarClient db,
            SecretProtector protector, AuthSessionService sessions, HttpContext http) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null || account.Status != "active" || account.PasswordHash is null)
                return Results.Unauthorized();

            if (!BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
                return Results.Unauthorized();

            if (account.TotpEnabled)
            {
                if (string.IsNullOrEmpty(req.TotpCode))
                    return Results.Json(new { error = "totp_required" }, statusCode: 403);

                var backupCodesBefore = account.TotpBackupCodes;
                if (!VerifyTotp(account, req.TotpCode, protector, out var consumedBackupCode))
                    return Results.Unauthorized();
                if (consumedBackupCode)
                {
                    var consumed = await db.Updateable<UserAccountEntity>()
                        .SetColumns(x => new UserAccountEntity
                        {
                            TotpBackupCodes = account.TotpBackupCodes,
                            LastLoginAt = DateTime.UtcNow,
                        })
                        .Where(x => x.Id == account.Id && x.TotpBackupCodes == backupCodesBefore)
                        .ExecuteCommandAsync();
                    if (consumed != 1) return Results.Unauthorized();
                }
                else
                {
                    account.LastLoginAt = DateTime.UtcNow;
                    await db.Updateable(account).UpdateColumns(x => x.LastLoginAt)
                        .ExecuteCommandAsync();
                }
            }
            else
            {
                account.LastLoginAt = DateTime.UtcNow;
                await db.Updateable(account).UpdateColumns(x => x.LastLoginAt)
                    .ExecuteCommandAsync();
            }

            var tokens = await sessions.IssueAsync(account.Id, account.Email, account.Role,
                http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent);
            return Results.Ok(new
            {
                token = tokens.Token, refresh_token = tokens.RefreshToken,
                expires_at = tokens.ExpiresAt, session_id = tokens.SessionId,
                email = account.Email, role = account.Role
            });
        });

        auth.MapPost("/refresh", async (RefreshRequest req, AuthSessionService sessions,
            HttpContext http) =>
        {
            var tokens = await sessions.RotateAsync(req.RefreshToken,
                http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent);
            return tokens is null
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    token = tokens.Token, refresh_token = tokens.RefreshToken,
                    expires_at = tokens.ExpiresAt, session_id = tokens.SessionId
                });
        });

        auth.MapPost("/password-reset/request", async (PasswordResetRequest req,
            PasswordResetService resets, IConfiguration config, IWebHostEnvironment environment) =>
        {
            var issued = await resets.IssueAsync(req.Email);
            // Keep the public response identical for unknown and known addresses.
            if (issued is not null
                && (environment.IsDevelopment()
                    || string.Equals(config["PasswordReset:ExposeToken"], "true",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Accepted(value: new
                {
                    accepted = true,
                    debug_token = issued.Token,
                    expires_at = issued.ExpiresAt,
                });
            }

            return Results.Accepted(value: new { accepted = true });
        });

        auth.MapPost("/password-reset/confirm", async (PasswordResetConfirmRequest req,
            PasswordResetService resets) =>
        {
            if (!await resets.ConsumeAsync(req.Token, req.NewPassword))
                return Results.BadRequest(new { error = "Invalid or expired reset token" });
            return Results.NoContent();
        });

        auth.MapPost("/oauth/callback", async (OAuthCallbackRequest req, ISqlSugarClient db,
            IConfiguration config, IHttpClientFactory httpFactory,
            ListingRepository registry, AuthSessionService sessions, IClusterClient client,
            HttpContext http) =>
        {
            var (email, oauthId) = await ExchangeOAuthCode(req, config, httpFactory);
            if (email is null)
                return Results.BadRequest(new { error = "OAuth exchange failed" });

            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.OAuthProvider == req.Provider && x.OAuthId == oauthId).FirstAsync();
            var createdIdentity = false;

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
                    account.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
                        "SELECT id FROM user_accounts WHERE email = @email",
                        new SugarParameter("@email", email)));
                    createdIdentity = true;
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

            // OAuth may find an existing identity or create one; either way make
            // sure the product-owned registry can discover the user aggregate.
            await registry.RegisterInteger("user", account.Id);
            if (createdIdentity)
                await client.GetGrain<IUserGrain>(account.Id).Create(new UserUpsert(
                    "user", 0m, 1, 0, []));
            var tokens = await sessions.IssueAsync(account.Id, account.Email, account.Role,
                http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent);

            return Results.Ok(new
            {
                token = tokens.Token, refresh_token = tokens.RefreshToken,
                expires_at = tokens.ExpiresAt, session_id = tokens.SessionId,
                email = account.Email, role = account.Role
            });
        });

        user.MapPost("/totp/setup", async (ClaimsPrincipal principal, ISqlSugarClient db,
            SecretProtector protector) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null) return Results.NotFound();

            var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
            account.TotpSecret = protector.Protect(secret);
            await db.Updateable(account).UpdateColumns(x => x.TotpSecret).ExecuteCommandAsync();

            var qrUri = $"otpauth://totp/ScalaAPI:{email}?secret={secret}&issuer=ScalaAPI";
            return Results.Ok(new TotpSetupResponse(secret, qrUri));
        });

        user.MapPost("/totp/verify", async (ClaimsPrincipal principal, TotpVerifyRequest req,
            ISqlSugarClient db, SecretProtector protector) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null || account.TotpSecret is null) return Results.NotFound();

            if (!VerifyTotp(account, req.Code, protector, out _))
                return Results.BadRequest(new { error = "Invalid TOTP code" });

            var backupCodes = GenerateBackupCodes();
            account.TotpEnabled = true;
            account.TotpBackupCodes = string.Join(",",
                backupCodes.Select(BCrypt.Net.BCrypt.HashPassword));
            await db.Updateable(account)
                .UpdateColumns(x => new { x.TotpEnabled, x.TotpBackupCodes }).ExecuteCommandAsync();

            return Results.Ok(new { backup_codes = backupCodes });
        });

        user.MapPost("/totp/disable", async (ClaimsPrincipal principal, TotpVerifyRequest req,
            ISqlSugarClient db, SecretProtector protector) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null) return Results.NotFound();

            if (!VerifyTotp(account, req.Code, protector, out _))
                return Results.BadRequest(new { error = "Invalid TOTP code" });

            account.TotpEnabled = false;
            account.TotpSecret = null;
            account.TotpBackupCodes = null;
            await db.Updateable(account)
                .UpdateColumns(x => new { x.TotpEnabled, x.TotpSecret, x.TotpBackupCodes })
                .ExecuteCommandAsync();

            return Results.Ok(new { message = "2FA disabled" });
        });

        user.MapPost("/logout", async (ClaimsPrincipal principal, AuthSessionService sessions,
            HttpContext http) =>
        {
            var sessionId = AuthClaims.SessionId(principal);
            if (string.IsNullOrWhiteSpace(sessionId))
                sessionId = AuthSessionService.SessionIdFromAuthorization(
                    http.Request.Headers.Authorization.ToString());
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.Unauthorized();
            await sessions.RevokeSessionAsync(sessionId);
            return Results.NoContent();
        });

        user.MapGet("/sessions", async (ClaimsPrincipal principal,
            AuthSessionService sessions, HttpContext http) =>
        {
            var sessionId = AuthClaims.SessionId(principal);
            if (string.IsNullOrWhiteSpace(sessionId))
                sessionId = AuthSessionService.SessionIdFromAuthorization(
                    http.Request.Headers.Authorization.ToString());
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                userId = sessionId is null ? 0 : await sessions.GetUserIdAsync(sessionId) ?? 0;
            if (userId <= 0) return Results.Unauthorized();
            return Results.Ok(await sessions.ListAsync(userId));
        });

        user.MapDelete("/sessions/{sessionId}", async (string sessionId,
            ClaimsPrincipal principal, AuthSessionService sessions, HttpContext http) =>
        {
            var currentSessionId = AuthClaims.SessionId(principal);
            if (string.IsNullOrWhiteSpace(currentSessionId))
                currentSessionId = AuthSessionService.SessionIdFromAuthorization(
                    http.Request.Headers.Authorization.ToString());
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                userId = currentSessionId is null ? 0 : await sessions.GetUserIdAsync(currentSessionId) ?? 0;
            if (userId <= 0) return Results.Unauthorized();
            return await sessions.RevokeAsync(userId, sessionId)
                ? Results.NoContent() : Results.NotFound();
        });
    }

    private static bool VerifyTotp(UserAccountEntity account, string code,
        SecretProtector protector, out bool backupCodeConsumed)
    {
        backupCodeConsumed = false;
        if (account.TotpSecret is null) return false;

        var secretBytes = Base32Encoding.ToBytes(protector.Unprotect(account.TotpSecret));
        var totp = new Totp(secretBytes);

        if (totp.VerifyTotp(code, out _, new VerificationWindow(0, 0)))
            return true;

        if (account.TotpBackupCodes is not null)
        {
            var codes = account.TotpBackupCodes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var usedIndex = Array.FindIndex(codes, hash => BCrypt.Net.BCrypt.Verify(code, hash));
            if (usedIndex >= 0)
            {
                var remaining = codes.Where((_, index) => index != usedIndex).ToArray();
                account.TotpBackupCodes = string.Join(",", remaining);
                backupCodeConsumed = true;
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ScalaAPI");
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
