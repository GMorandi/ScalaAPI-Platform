using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using OtpNet;
using SqlSugar;
using Orleans;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Data.Entities;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public record RegisterRequest(string Email, string Password, string? DisplayName);
public record UserLoginRequest(string Email, string Password, string? TotpCode);
public record RefreshRequest(string RefreshToken);
public record OAuthCallbackRequest(string Provider, string Code, string RedirectUri,
    string State, string CodeVerifier);
public record OAuthStartResponse(string Provider, string RedirectUri, string State,
    string CodeVerifier, string CodeChallenge, string AuthorizationUrl, DateTime ExpiresAt);
public record TotpSetupResponse(string Secret, string QrUri);
public record TotpVerifyRequest(string Code);
public record PasswordResetRequest(string Email);
public record PasswordResetConfirmRequest(string Token, string NewPassword);
public record EmailVerificationRequest(string Email);
public record EmailVerificationConfirmRequest(string Token);
public record ProfileUpdateRequest(string? DisplayName);
public record PasswordChangeRequest(string CurrentPassword, string NewPassword);
public record AccountDeletionRequest(string Password, bool Confirm);

public static class UserAuthEndpoints
{
    public static void MapUserAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").AllowAnonymous();
        var user = app.MapGroup("/user").RequireAuthorization("UserOnly");

        user.MapGet("/profile", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            return account is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    account.Id, account.Email, account.DisplayName, account.Role,
                    account.Status, account.EmailVerified, account.EmailVerifiedAt,
                    account.TotpEnabled, account.CreatedAt,
                });
        });

        user.MapPut("/profile", async (ClaimsPrincipal principal, ProfileUpdateRequest req,
            ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
            var displayName = string.IsNullOrWhiteSpace(req.DisplayName)
                ? null : req.DisplayName.Trim();
            if (displayName?.Length > 200)
                return Results.BadRequest(new { error = "Display name is too long" });
            var changed = await db.Updateable<UserAccountEntity>()
                .SetColumns(x => x.DisplayName == displayName)
                .Where(x => x.Email == email && x.Status == "active")
                .ExecuteCommandAsync();
            return changed == 1 ? Results.NoContent() : Results.NotFound();
        });

        user.MapPost("/password", async (ClaimsPrincipal principal,
            PasswordChangeRequest req, ISqlSugarClient db,
            AuthSessionService sessions, HttpContext http) =>
        {
            var email = principal.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 12)
                return Results.BadRequest(new { error = "Password must be at least 12 characters" });
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email && x.Status == "active").FirstAsync();
            if (account is null || account.PasswordHash is null
                || !BCrypt.Net.BCrypt.Verify(req.CurrentPassword, account.PasswordHash))
                return Results.Unauthorized();

            account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
            await db.Updateable(account).UpdateColumns(x => x.PasswordHash).ExecuteCommandAsync();
            var sessionId = AuthClaims.SessionId(principal)
                ?? AuthSessionService.SessionIdFromAuthorization(
                    http.Request.Headers.Authorization.ToString());
            if (!string.IsNullOrWhiteSpace(sessionId))
                await sessions.RevokeOtherSessionsAsync(account.Id, sessionId);
            return Results.NoContent();
        });

        user.MapDelete("/account", async (ClaimsPrincipal principal,
            HttpRequest request, ISqlSugarClient db, IClusterClient client,
            ListingRepository registry, AuthSessionService sessions, HttpContext http) =>
        {
            var req = await request.ReadFromJsonAsync<AccountDeletionRequest>();
            if (req is null) return Results.BadRequest(new { error = "Request body is required" });
            if (!req.Confirm) return Results.BadRequest(new { error = "Confirmation required" });
            if (string.Equals(principal.FindFirst(ClaimTypes.Role)?.Value, "admin",
                    StringComparison.OrdinalIgnoreCase))
                return Results.Forbid();
            var email = principal.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email && x.Status == "active").FirstAsync();
            if (account is null || account.PasswordHash is null
                || !BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
                return Results.Unauthorized();

            var keyHashes = await db.Queryable<UserApiKeyEntity>()
                .Where(x => x.UserEmail == email && x.Status == "active")
                .Select(x => x.KeyHash).ToListAsync();
            db.Ado.BeginTran();
            try
            {
                await db.Updateable<UserAccountEntity>()
                    .SetColumns(x => new UserAccountEntity
                    {
                        Status = "deleted",
                        DisplayName = null,
                        PasswordHash = null,
                        EmailVerified = false,
                        EmailVerifiedAt = null,
                        TotpEnabled = false,
                        TotpSecret = null,
                        TotpBackupCodes = null,
                    })
                    .Where(x => x.Id == account.Id && x.Status == "active")
                    .ExecuteCommandAsync();
                await db.Updateable<UserApiKeyEntity>()
                    .SetColumns(x => x.Status == "revoked")
                    .Where(x => x.UserEmail == email && x.Status == "active")
                    .ExecuteCommandAsync();
                db.Ado.CommitTran();
            }
            catch
            {
                db.Ado.RollbackTran();
                throw;
            }

            foreach (var hash in keyHashes)
            {
                await client.GetGrain<IApiKeyGrain>(hash).Revoke();
                await registry.Unregister("apiKey", hash);
            }
            await client.GetGrain<IUserGrain>(account.Id).SetStatus("deleted");
            await registry.Unregister("user", account.Id.ToString());
            await sessions.RevokeAllAsync(account.Id);
            return Results.NoContent();
        });

        auth.MapPost("/register", async (RegisterRequest req, ISqlSugarClient db,
            IClusterClient client, ListingRepository registry, AccountingStore accounting,
            AuthAbuseService abuse, HttpContext http) =>
        {
            var ipAddress = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var gate = await abuse.CheckRegistrationAsync(ipAddress, http.RequestAborted);
            if (!gate.Allowed)
            {
                http.Response.Headers["Retry-After"] = gate.RetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = "auth_rate_limited" }, statusCode: 429);
            }

            if (!AuthInputValidation.TryNormalizeEmail(req.Email, out var email))
            {
                await abuse.RecordRegistrationFailureAsync(ipAddress, http.RequestAborted);
                return Results.BadRequest(new { error = "A valid email is required" });
            }
            if (!AuthInputValidation.IsValidPassword(req.Password))
            {
                await abuse.RecordRegistrationFailureAsync(ipAddress, http.RequestAborted);
                return Results.BadRequest(new { error = "Password must be 12 to 256 characters" });
            }
            var displayName = AuthInputValidation.NormalizeDisplayName(req.DisplayName);
            if (displayName?.Length > 200)
            {
                await abuse.RecordRegistrationFailureAsync(ipAddress, http.RequestAborted);
                return Results.BadRequest(new { error = "Display name is too long" });
            }

            var existing = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (existing is not null)
            {
                await abuse.RecordRegistrationFailureAsync(ipAddress, http.RequestAborted);
                return Results.Conflict(new { error = "Email already registered" });
            }

            var account = new UserAccountEntity
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                DisplayName = displayName,
                CreatedAt = DateTime.UtcNow,
            };
            try
            {
                await db.Insertable(account).ExecuteCommandAsync();
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                await abuse.RecordRegistrationFailureAsync(ipAddress, http.RequestAborted);
                return Results.Conflict(new { error = "Email already registered" });
            }
            account.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
                "SELECT id FROM user_accounts WHERE email = @email",
                new SugarParameter("@email", email)));
            await client.GetGrain<IUserGrain>(account.Id).Create(new UserCreate(
                "user", 1, 0, []));
            await registry.RegisterInteger("user", account.Id);
            await accounting.EnsureAccountAsync(account.Id);
            await abuse.RecordRegistrationSuccessAsync(ipAddress, http.RequestAborted);

            return Results.Ok(new { id = account.Id, email = account.Email });
        });

        auth.MapPost("/login", async (UserLoginRequest req, ISqlSugarClient db,
            TotpVerificationService totp, AuthSessionService sessions,
            AuthAbuseService abuse, HttpContext http) =>
        {
            var ipAddress = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var normalizedEmail = AuthInputValidation.TryNormalizeEmail(req.Email, out var email)
                ? email : null;
            var gate = await abuse.CheckLoginAsync(normalizedEmail, ipAddress, http.RequestAborted);
            if (!gate.Allowed)
            {
                http.Response.Headers["Retry-After"] = gate.RetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = "auth_rate_limited" }, statusCode: 429);
            }
            if (normalizedEmail is null)
            {
                await abuse.RecordLoginFailureAsync(null, ipAddress, http.RequestAborted);
                return Results.Unauthorized();
            }
            if (!AuthInputValidation.IsValidPassword(req.Password))
            {
                await abuse.RecordLoginFailureAsync(email, ipAddress, http.RequestAborted);
                return Results.Unauthorized();
            }

            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null || account.Status != "active" || account.PasswordHash is null)
            {
                await abuse.RecordLoginFailureAsync(email, ipAddress, http.RequestAborted);
                return Results.Unauthorized();
            }

            if (!BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
            {
                await abuse.RecordLoginFailureAsync(email, ipAddress, http.RequestAborted);
                return Results.Unauthorized();
            }

            if (account.TotpEnabled)
            {
                if (string.IsNullOrEmpty(req.TotpCode))
                    return Results.Json(new { error = "totp_required" }, statusCode: 403);

                var verification = await totp.VerifyAsync(account.Id, req.TotpCode!,
                    allowBackupCodes: true, http.RequestAborted);
                if (!verification.Accepted)
                {
                    if (verification.Status == TotpVerificationStatus.Locked)
                    {
                        http.Response.Headers["Retry-After"] =
                            verification.RetryAfterSeconds.ToString(
                                System.Globalization.CultureInfo.InvariantCulture);
                        return Results.Json(new { error = "totp_locked" }, statusCode: 429);
                    }
                    return Results.Unauthorized();
                }
            }

            account.LastLoginAt = DateTime.UtcNow;
            await db.Updateable(account).UpdateColumns(x => x.LastLoginAt)
                .ExecuteCommandAsync();

            var tokens = await sessions.IssueAsync(account.Id, account.Email, account.Role,
                http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent);
            await abuse.RecordLoginSuccessAsync(email, ipAddress, http.RequestAborted);
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

        auth.MapPost("/email-verification/request", async (EmailVerificationRequest req,
            EmailVerificationService verification, IConfiguration config,
            IWebHostEnvironment environment) =>
        {
            var issued = await verification.IssueAsync(req.Email);
            if (issued is not null
                && (environment.IsDevelopment()
                    || string.Equals(config["EmailVerification:ExposeToken"], "true",
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

        auth.MapPost("/email-verification/confirm", async (
            EmailVerificationConfirmRequest req, EmailVerificationService verification) =>
        {
            return await verification.ConsumeAsync(req.Token)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "Invalid or expired verification token" });
        });

        auth.MapGet("/oauth/{provider}/start", async (string provider, string redirectUri,
            OAuthStateService states, IConfiguration config, CancellationToken ct) =>
        {
            var normalizedProvider = OAuthStateService.NormalizeProvider(provider);
            var normalizedRedirect = OAuthStateService.NormalizeRedirectUri(redirectUri);
            if (normalizedProvider is null || normalizedRedirect is null)
                return Results.BadRequest(new { error = "Unsupported provider or redirect URI" });

            var configProvider = normalizedProvider == "github" ? "GitHub" : "Google";
            var clientId = config[$"OAuth:{configProvider}:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var authorizationEndpoint = OAuthEndpoint(config, configProvider,
                "AuthorizationEndpoint", normalizedProvider == "github"
                    ? "https://github.com/login/oauth/authorize"
                    : "https://accounts.google.com/o/oauth2/v2/auth");
            if (authorizationEndpoint is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var issued = await states.IssueAsync(normalizedProvider, normalizedRedirect, ct);
            if (issued is null) return Results.BadRequest(new { error = "Invalid OAuth request" });
            var parameters = new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = issued.RedirectUri,
                ["response_type"] = "code",
                ["state"] = issued.State,
                ["code_challenge"] = issued.CodeChallenge,
                ["code_challenge_method"] = "S256",
            };
            var authorizationUrl = normalizedProvider == "github"
                ? QueryHelpers.AddQueryString(authorizationEndpoint, parameters)
                : QueryHelpers.AddQueryString(authorizationEndpoint,
                    parameters.Concat(new Dictionary<string, string?>
                    {
                        ["scope"] = "openid email profile",
                    }));
            return Results.Ok(new OAuthStartResponse(issued.Provider, issued.RedirectUri,
                issued.State, issued.CodeVerifier, issued.CodeChallenge, authorizationUrl,
                issued.ExpiresAt));
        });

        auth.MapPost("/oauth/callback", async (OAuthCallbackRequest req, ISqlSugarClient db,
            IConfiguration config, IHttpClientFactory httpFactory,
            ListingRepository registry, AuthSessionService sessions, IClusterClient client,
            AccountingStore accounting, OAuthStateService states, HttpContext http) =>
        {
            var state = await states.ConsumeAsync(req.Provider, req.State, req.RedirectUri,
                req.CodeVerifier, http.RequestAborted);
            if (!state.Accepted)
                return Results.BadRequest(new { error = $"oauth_state_{state.Status.ToString().ToLowerInvariant()}" });

            var (email, oauthId) = await ExchangeOAuthCode(req, config, httpFactory,
                http.RequestAborted);
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
                await client.GetGrain<IUserGrain>(account.Id).Create(new UserCreate(
                    "user", 1, 0, []));
            await accounting.EnsureAccountAsync(account.Id);
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
            if (account.TotpEnabled)
                return Results.Conflict(new { error = "TOTP is already enabled" });

            var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
            account.TotpSecret = protector.Protect(secret);
            await db.Updateable(account).UpdateColumns(x => x.TotpSecret).ExecuteCommandAsync();

            var qrUri = $"otpauth://totp/ScalaAPI:{email}?secret={secret}&issuer=ScalaAPI";
            return Results.Ok(new TotpSetupResponse(secret, qrUri));
        });

        user.MapPost("/totp/verify", async (ClaimsPrincipal principal, TotpVerifyRequest req,
            ISqlSugarClient db, TotpVerificationService totp, HttpContext http) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null || account.TotpSecret is null) return Results.NotFound();
            var backupCodes = GenerateBackupCodes();
            var verification = await totp.EnableAsync(account.Id, req.Code,
                backupCodes.Select(BCrypt.Net.BCrypt.HashPassword).ToArray());
            if (verification.Status == TotpVerificationStatus.Locked)
            {
                http.Response.Headers["Retry-After"] = verification.RetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = "totp_locked" }, statusCode: 429);
            }
            if (!verification.Accepted)
                return Results.BadRequest(new { error = "Invalid TOTP code" });

            return Results.Ok(new { backup_codes = backupCodes });
        });

        user.MapPost("/totp/disable", async (ClaimsPrincipal principal, TotpVerifyRequest req,
            ISqlSugarClient db, TotpVerificationService totp, HttpContext http) =>
        {
            var email = principal.Identity?.Name;
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email).FirstAsync();
            if (account is null) return Results.NotFound();

            var verification = await totp.DisableAsync(account.Id, req.Code);
            if (verification.Status == TotpVerificationStatus.Locked)
            {
                http.Response.Headers["Retry-After"] = verification.RetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = "totp_locked" }, statusCode: 429);
            }
            if (!verification.Accepted)
                return Results.BadRequest(new { error = "Invalid TOTP code" });

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

    private static string[] GenerateBackupCodes()
    {
        var codes = new string[10];
        for (int i = 0; i < 10; i++)
            codes[i] = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        return codes;
    }

    private static async Task<(string? Email, string? OAuthId)> ExchangeOAuthCode(
        OAuthCallbackRequest req, IConfiguration config, IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var client = httpFactory.CreateClient();

        if (req.Provider == "github")
        {
            var clientId = config["OAuth:GitHub:ClientId"];
            var clientSecret = config["OAuth:GitHub:ClientSecret"];
            var tokenEndpoint = OAuthEndpoint(config, "GitHub", "TokenEndpoint",
                "https://github.com/login/oauth/access_token");
            var userEndpoint = OAuthEndpoint(config, "GitHub", "UserEndpoint",
                "https://api.github.com/user");
            if (tokenEndpoint is null || userEndpoint is null
                || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                return (null, null);

            var tokenResp = await client.PostAsync(tokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["code"] = req.Code,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = req.RedirectUri,
                    ["code_verifier"] = req.CodeVerifier,
                }), ct);
            var accessToken = await ReadAccessTokenAsync(tokenResp.Content, ct);
            if (accessToken is null)
                return (null, null);

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ScalaAPI");
            var userResp = await client.GetAsync($"{userEndpoint.TrimEnd('/')}/emails", ct);
            var emails = await userResp.Content.ReadFromJsonAsync<List<GitHubEmail>>(ct);
            var primary = emails?.FirstOrDefault(e => e.Primary && e.Verified)?.Email;
            var userResp2 = await client.GetAsync(userEndpoint, ct);
            var userData = await userResp2.Content.ReadFromJsonAsync<Dictionary<string, object>>(ct);
            var id = userData?.GetValueOrDefault("id")?.ToString();

            return (primary, id);
        }

        if (req.Provider == "google")
        {
            var clientId = config["OAuth:Google:ClientId"];
            var clientSecret = config["OAuth:Google:ClientSecret"];
            var tokenEndpoint = OAuthEndpoint(config, "Google", "TokenEndpoint",
                "https://oauth2.googleapis.com/token");
            var userEndpoint = OAuthEndpoint(config, "Google", "UserEndpoint",
                "https://www.googleapis.com/oauth2/v2/userinfo");
            if (tokenEndpoint is null || userEndpoint is null
                || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                return (null, null);

            var tokenResp = await client.PostAsync(tokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId!,
                    ["client_secret"] = clientSecret!,
                    ["code"] = req.Code,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = req.RedirectUri,
                    ["code_verifier"] = req.CodeVerifier,
                }));
            var accessToken = await ReadAccessTokenAsync(tokenResp.Content, ct);
            if (accessToken is null)
                return (null, null);

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var userResp = await client.GetAsync(userEndpoint, ct);
            var userData = await userResp.Content.ReadFromJsonAsync<Dictionary<string, object>>(ct);
            var email = userData?.GetValueOrDefault("email")?.ToString();
            var id = userData?.GetValueOrDefault("id")?.ToString();

            return (email, id);
        }

        return (null, null);
    }

    private static async Task<string?> ReadAccessTokenAsync(HttpContent content,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.TryGetProperty("access_token", out var accessToken)
            && accessToken.ValueKind == JsonValueKind.String
            ? accessToken.GetString()
            : null;
    }

    private static string? OAuthEndpoint(IConfiguration config, string provider,
        string name, string fallback)
    {
        var value = config[$"OAuth:{provider}:{name}"] ?? fallback;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri.TrimEnd('/')
            : null;
    }

    private record GitHubEmail(string Email, bool Primary, bool Verified);
}
