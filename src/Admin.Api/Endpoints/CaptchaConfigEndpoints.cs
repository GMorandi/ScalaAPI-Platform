using ScalaAPI.Admin.Auth;

namespace ScalaAPI.Admin.Endpoints;

public static class CaptchaConfigEndpoints
{
    public static void MapCaptchaConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/captcha").RequireAuthorization("AdminOnly");

        group.MapGet("/domain-quotas", async (EmailDomainQuotaService quota, CancellationToken ct) =>
        {
            var quotas = await quota.ListAsync(ct);
            return Results.Ok(quotas.Select(q => new { q.Domain, q.Count, q.Limit }));
        });

        group.MapPut("/domain-quotas/{domain}", async (string domain, SetDomainLimitRequest req,
            EmailDomainQuotaService quota, CancellationToken ct) =>
        {
            if (req.Limit < 1 || req.Limit > 100_000)
                return Results.BadRequest(new { error = "invalid_limit" });
            await quota.SetDomainLimitAsync(domain.ToLowerInvariant(), req.Limit, ct);
            return Results.Ok(new { status = "updated" });
        });
    }
}

public record SetDomainLimitRequest(int Limit);
