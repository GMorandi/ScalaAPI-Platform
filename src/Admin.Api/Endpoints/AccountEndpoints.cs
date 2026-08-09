using Orleans;
using ScalaAPI.Admin.Data;
using ScalaAPI.Admin.Models;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/accounts").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IClusterClient client, ListingRepository repo, int page = 0, int size = 20) =>
        {
            var ids = await repo.GetIntegerGrainIds("account", page, size);
            var total = await repo.CountGrains("account");
            var items = new List<AccountProjection>();
            foreach (var id in ids)
            {
                var grain = client.GetGrain<IAccountGrain>(id);
                items.Add(await grain.GetProjection());
            }
            return Results.Ok(new PagedResponse<AccountProjection>(items, total, page, size));
        });

        group.MapGet("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            var details = await grain.GetDetails();
            return Results.Ok(details);
        });

        group.MapPost("/", async (AccountCreateRequest req, IClusterClient client, ListingRepository repo) =>
        {
            var validation = Validate(req, requireOAuth: true);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            var allocator = client.GetGrain<IIdAllocatorGrain>("account");
            var id = await allocator.Next();
            var grain = client.GetGrain<IAccountGrain>(id);
            await grain.Create(new AccountUpsert(
                req.Name, req.Platform, req.Type, req.BaseUrl,
                req.Priority, req.Concurrency, req.LoadFactor, req.RateMultiplier,
                req.Schedulable, req.Credentials, req.ModelMapping,
                req.SupportedModels, req.ProxyUrl, req.TlsFingerprint, req.OAuth));
            await repo.RegisterInteger("account", id);
            return Results.Created($"/admin/accounts/{id}", new { id });
        });

        group.MapPut("/{id:long}", async (long id, AccountCreateRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            var existing = await grain.GetDetails();
            var validation = Validate(req,
                requireOAuth: string.Equals(req.Type, "oauth", StringComparison.OrdinalIgnoreCase)
                    && existing.OAuth is null);
            if (validation is not null) return Results.BadRequest(new { error = validation });
            await grain.Update(new AccountUpsert(
                req.Name, req.Platform, req.Type, req.BaseUrl,
                req.Priority, req.Concurrency, req.LoadFactor, req.RateMultiplier,
                req.Schedulable, req.Credentials, req.ModelMapping,
                req.SupportedModels, req.ProxyUrl, req.TlsFingerprint, req.OAuth));
            return Results.NoContent();
        });

        group.MapPatch("/{id:long}/status", async (long id, StatusRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            await grain.SetStatus(req.Status);
            return Results.NoContent();
        });

        group.MapDelete("/{id:long}", async (long id, IClusterClient client, ListingRepository repo) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            await grain.Delete();
            await repo.Unregister("account", id.ToString());
            return Results.NoContent();
        });
    }

    private static string? Validate(AccountCreateRequest req, bool requireOAuth)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Platform)
            || string.IsNullOrWhiteSpace(req.BaseUrl))
            return "Name, platform, and base URL are required";
        if (!Uri.TryCreate(req.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            return "Base URL must be absolute HTTP or HTTPS";
        if (req.Credentials.Count > 32
            || req.Credentials.Any(item => !IsHeaderName(item.Key)
                || item.Value.Length > 4096
                || item.Value.IndexOfAny(['\r', '\n']) >= 0))
            return "Static credential headers are invalid";
        if (!string.Equals(req.Type, "oauth", StringComparison.OrdinalIgnoreCase))
            return null;
        if (req.OAuth is null)
            return requireOAuth ? "OAuth configuration is required" : null;
        if (!Uri.TryCreate(req.OAuth.TokenEndpoint, UriKind.Absolute, out var tokenUri)
            || tokenUri.Scheme is not ("http" or "https"))
            return "OAuth token endpoint must be absolute HTTP or HTTPS";
        if (string.IsNullOrWhiteSpace(req.OAuth.ClientId)
            || string.IsNullOrWhiteSpace(req.OAuth.RefreshToken)
            || string.IsNullOrWhiteSpace(req.OAuth.AccessToken)
            || req.OAuth.ExpiresAtUnixSeconds <= 0)
            return "OAuth client, tokens, and expiry are required";
        if (!IsHeaderName(req.OAuth.HeaderName)
            || (!string.IsNullOrEmpty(req.OAuth.HeaderScheme)
                && (req.OAuth.HeaderScheme.Length > 32
                    || !req.OAuth.HeaderScheme.All(IsTokenTypeCharacter))))
            return "OAuth header name or scheme is invalid";
        return null;
    }

    private static bool IsHeaderName(string value) => value.Length is > 0 and <= 64
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

    private static bool IsTokenTypeCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '~' or '-';
}
