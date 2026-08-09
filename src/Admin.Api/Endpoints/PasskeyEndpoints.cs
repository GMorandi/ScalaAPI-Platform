using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.WebUtilities;
using SqlSugar;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Data.Entities;

namespace ScalaAPI.Admin.Endpoints;

public sealed record PasskeyLoginOptionsRequest(string Email);

public static class PasskeyEndpoints
{
    public static void MapPasskeyEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").AllowAnonymous();
        var user = app.MapGroup("/user").RequireAuthorization("UserOnly");

        user.MapGet("/passkeys", async (ClaimsPrincipal principal,
            PasskeyStore passkeys, CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            var credentials = await passkeys.ListCredentialsAsync(userId, ct);
            return Results.Ok(new
            {
                items = credentials.Select(credential => new
                {
                    id = WebEncoders.Base64UrlEncode(credential.CredentialId),
                    credential.DisplayName,
                    credential.CreatedAt,
                    credential.LastUsedAt,
                }),
            });
        });

        user.MapPost("/passkeys/register/options", async (ClaimsPrincipal principal,
            ISqlSugarClient db, Fido2 fido2, PasskeyStore passkeys,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Id == userId && x.Status == "active").FirstAsync();
            if (account is null) return Results.Unauthorized();

            var existing = await passkeys.ListCredentialsAsync(userId, ct);
            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = new Fido2User
                {
                    Name = account.Email,
                    DisplayName = account.DisplayName ?? account.Email,
                    Id = UserHandle(userId),
                },
                ExcludeCredentials = existing
                    .Select(item => new PublicKeyCredentialDescriptor(item.CredentialId))
                    .ToArray(),
                AuthenticatorSelection = AuthenticatorSelection.Default,
                AttestationPreference = AttestationConveyancePreference.None,
            });
            var challengeId = await passkeys.CreateChallengeAsync(
                userId, "registration", options.ToJson(), DateTime.UtcNow.AddMinutes(5), ct);
            return Results.Ok(new
            {
                challenge_id = challengeId,
                options = JsonSerializer.Deserialize<JsonElement>(options.ToJson()),
            });
        });

        user.MapPost("/passkeys/register/{challengeId:guid}", async (
            Guid challengeId,
            AuthenticatorAttestationRawResponse response,
            ClaimsPrincipal principal,
            Fido2 fido2,
            PasskeyStore passkeys,
            HttpRequest request,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            var challenge = await passkeys.GetChallengeAsync(
                challengeId, userId, "registration", ct);
            if (challenge is null) return Results.BadRequest(new { error = "passkey_challenge_invalid" });

            CredentialCreateOptions options;
            try
            {
                options = CredentialCreateOptions.FromJson(challenge.OptionsJson);
                var result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = response,
                    OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = async (args, callbackCt) =>
                        !await passkeys.CredentialExistsAsync(args.CredentialId, callbackCt),
                }, ct);
                if (!await passkeys.CompleteRegistrationAsync(
                        challengeId, userId, userId, result.Id, UserHandle(userId),
                        result.PublicKey, result.SignCount, "Passkey",
                        request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct))
                    return Results.Conflict(new { error = "passkey_challenge_replayed" });
                return Results.Ok(new
                {
                    id = WebEncoders.Base64UrlEncode(result.Id),
                    sign_count = result.SignCount,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Results.BadRequest(new { error = "passkey_registration_failed" });
            }
        });

        auth.MapPost("/passkeys/options", async (
            PasskeyLoginOptionsRequest request,
            ISqlSugarClient db,
            Fido2 fido2,
            PasskeyStore passkeys,
            CancellationToken ct) =>
        {
            if (!AuthInputValidation.TryNormalizeEmail(request.Email, out var email))
                return Results.Unauthorized();
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email && x.Status == "active").FirstAsync();
            if (account is null) return Results.Unauthorized();
            var credentials = await passkeys.ListCredentialsAsync(account.Id, ct);
            if (credentials.Count == 0) return Results.Unauthorized();
            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = credentials
                    .Select(item => new PublicKeyCredentialDescriptor(item.CredentialId))
                    .ToArray(),
                UserVerification = UserVerificationRequirement.Preferred,
            });
            var challengeId = await passkeys.CreateChallengeAsync(
                account.Id, "authentication", options.ToJson(), DateTime.UtcNow.AddMinutes(5), ct);
            return Results.Ok(new
            {
                challenge_id = challengeId,
                options = JsonSerializer.Deserialize<JsonElement>(options.ToJson()),
            });
        });

        auth.MapPost("/passkeys/{challengeId:guid}", async (
            Guid challengeId,
            AuthenticatorAssertionRawResponse response,
            Fido2 fido2,
            PasskeyStore passkeys,
            ISqlSugarClient db,
            AuthSessionService sessions,
            HttpContext http,
            CancellationToken ct) =>
        {
            var credentialId = DecodeCredentialId(response);
            if (credentialId is null) return Results.Unauthorized();
            var credential = await passkeys.GetCredentialAsync(credentialId, ct);
            if (credential is null) return Results.Unauthorized();
            var challenge = await passkeys.GetChallengeByIdAsync(
                challengeId, "authentication", ct);
            if (challenge is null || challenge.UserId != credential.UserId)
                return Results.Unauthorized();

            try
            {
                var options = AssertionOptions.FromJson(challenge.OptionsJson);
                var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
                {
                    AssertionResponse = response,
                    OriginalOptions = options,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = credential.SignatureCounter,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                        Task.FromResult(args.CredentialId.SequenceEqual(credential.CredentialId)
                            && args.UserHandle.SequenceEqual(credential.UserHandle)),
                }, ct);
                if (!await passkeys.TryConsumeChallengeAsync(
                        challengeId, credential.UserId, "authentication", ct))
                    return Results.Conflict(new { error = "passkey_challenge_replayed" });
                if (!await passkeys.UpdateCounterAsync(
                        credential.CredentialId, result.SignCount, ct))
                    return Results.Unauthorized();

                var account = await db.Queryable<UserAccountEntity>()
                    .Where(x => x.Id == credential.UserId && x.Status == "active")
                    .FirstAsync();
                if (account is null) return Results.Unauthorized();
                var tokens = await sessions.IssueAsync(account.Id, account.Email,
                    account.Role, http.Connection.RemoteIpAddress?.ToString(),
                    http.Request.Headers.UserAgent);
                return Results.Ok(new
                {
                    token = tokens.Token, refresh_token = tokens.RefreshToken,
                    expires_at = tokens.ExpiresAt, session_id = tokens.SessionId,
                    email = account.Email, role = account.Role,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Results.Unauthorized();
            }
        });

        user.MapDelete("/passkeys/{credentialId}", async (
            string credentialId,
            ClaimsPrincipal principal,
            PasskeyStore passkeys,
            HttpRequest request,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            byte[] decoded;
            try { decoded = WebEncoders.Base64UrlDecode(credentialId); }
            catch (FormatException) { return Results.BadRequest(new { error = "invalid_credential_id" }); }
            var deleted = await passkeys.DeleteCredentialAsync(userId, userId, decoded,
                request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }

    private static byte[] UserHandle(long userId) =>
        Encoding.UTF8.GetBytes($"scalaapi:user:{userId}");

    private static byte[]? DecodeCredentialId(AuthenticatorAssertionRawResponse response)
    {
        if (response.RawId is { Length: > 0 }) return response.RawId;
        if (string.IsNullOrWhiteSpace(response.Id)) return null;
        try { return WebEncoders.Base64UrlDecode(response.Id); }
        catch (FormatException) { return null; }
    }
}
