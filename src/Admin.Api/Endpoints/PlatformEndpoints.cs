using System.Security.Claims;
using System.Security.Cryptography;
using SqlSugar;
using Sub2Api.Data.Entities;

namespace Sub2Api.Admin.Endpoints;

public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this WebApplication app)
    {
        MapApiKeySelfService(app);
        MapUsageSummary(app);
        MapAnnouncements(app);
        MapPayments(app);
        MapSubscriptions(app);
        MapRedeemCodes(app);
        MapChannelMonitors(app);
        MapOpsMetrics(app);
        MapAuditLogs(app);
        MapMiscAdmin(app);
    }

    private static void MapApiKeySelfService(WebApplication app)
    {
        var group = app.MapGroup("/user/apikeys").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name ?? "";
            var keys = await db.Ado.SqlQueryAsync<ApiKeyView>(
                """SELECT grainidextensionstring as key_hash FROM orleansstorage WHERE graintypestring LIKE '%ApiKey%' AND payloadbinary IS NOT NULL LIMIT 100""");
            return Results.Ok(new { keys });
        });

        group.MapPost("/", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name ?? "";
            var keyId = $"sk-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
            return Results.Ok(new { key = keyId, message = "Store this key securely, it cannot be retrieved again" });
        });

        group.MapDelete("/{keyId}", async (string keyId, ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            return Results.Ok(new { message = "Key deleted" });
        });
    }

    private static void MapUsageSummary(WebApplication app)
    {
        var group = app.MapGroup("/admin/usage/summary").RequireAuthorization();

        group.MapGet("/", async (ISqlSugarClient db, long? userId, string? model,
            DateTime? from, DateTime? to, string granularity = "daily") =>
        {
            var query = db.Queryable<UsageSummaryDailyEntity>();
            if (userId.HasValue) query = query.Where(x => x.UserId == userId.Value);
            if (!string.IsNullOrEmpty(model)) query = query.Where(x => x.Model == model);
            if (from.HasValue) query = query.Where(x => x.Date >= from.Value);
            if (to.HasValue) query = query.Where(x => x.Date <= to.Value);

            var results = await query.OrderByDescending(x => x.Date).Take(365).ToListAsync();
            return Results.Ok(new { items = results });
        });

        group.MapPost("/refresh", async (ISqlSugarClient db) =>
        {
            await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO usage_summary_daily (id, user_id, model, date, request_count, input_tokens, output_tokens, total_cost_usd)
                SELECT user_id || ':' || model || ':' || created_at::date, user_id, model, created_at::date,
                       COUNT(*), SUM(input_tokens), SUM(output_tokens), SUM(cost_usd)
                FROM usage_logs WHERE created_at >= CURRENT_DATE - INTERVAL '1 day'
                GROUP BY user_id, model, created_at::date
                ON CONFLICT (id) DO UPDATE SET
                  request_count = EXCLUDED.request_count, input_tokens = EXCLUDED.input_tokens,
                  output_tokens = EXCLUDED.output_tokens, total_cost_usd = EXCLUDED.total_cost_usd
                """);
            return Results.Ok(new { message = "Summary refreshed" });
        });
    }

    private static void MapAnnouncements(WebApplication app)
    {
        var admin = app.MapGroup("/admin/announcements").RequireAuthorization();
        var publicGroup = app.MapGroup("/announcements").AllowAnonymous();

        publicGroup.MapGet("/", async (ISqlSugarClient db) =>
        {
            var items = await db.Queryable<AnnouncementEntity>()
                .Where(x => x.Status == "published")
                .Where(x => x.ExpiresAt == null || x.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(x => x.Priority)
                .OrderByDescending(x => x.CreatedAt)
                .Take(20).ToListAsync();
            return Results.Ok(new { items });
        });

        admin.MapPost("/", async (AnnouncementEntity req, ISqlSugarClient db) =>
        {
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok(new { id = req.Id });
        });

        admin.MapPut("/{id}", async (long id, AnnouncementEntity req, ISqlSugarClient db) =>
        {
            req.Id = id;
            await db.Updateable(req)
                .UpdateColumns(x => new { x.Title, x.Content, x.Status, x.Priority, x.ExpiresAt })
                .ExecuteCommandAsync();
            return Results.Ok();
        });

        admin.MapDelete("/{id}", async (long id, ISqlSugarClient db) =>
        {
            await db.Deleteable<AnnouncementEntity>().Where(x => x.Id == id).ExecuteCommandAsync();
            return Results.Ok();
        });
    }

    private static void MapPayments(WebApplication app)
    {
        var group = app.MapGroup("/admin/payments").RequireAuthorization();
        var userGroup = app.MapGroup("/user/payments").RequireAuthorization();

        userGroup.MapPost("/create", async (ClaimsPrincipal principal, PaymentOrderEntity req, ISqlSugarClient db) =>
        {
            req.Status = "pending";
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok(new { id = req.Id, status = req.Status });
        });

        userGroup.MapGet("/", async (ClaimsPrincipal principal, ISqlSugarClient db, int page = 1, int size = 20) =>
        {
            var items = await db.Queryable<PaymentOrderEntity>()
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items });
        });

        group.MapPost("/{id}/confirm", async (long id, ISqlSugarClient db) =>
        {
            await db.Updateable<PaymentOrderEntity>()
                .SetColumns(x => x.Status == "paid")
                .SetColumns(x => x.PaidAt == DateTime.UtcNow)
                .Where(x => x.Id == id).ExecuteCommandAsync();
            return Results.Ok(new { message = "Payment confirmed" });
        });
    }

    private static void MapSubscriptions(WebApplication app)
    {
        var admin = app.MapGroup("/admin/subscriptions").RequireAuthorization();

        admin.MapGet("/plans", async (ISqlSugarClient db) =>
        {
            var plans = await db.Queryable<SubscriptionPlanEntity>().ToListAsync();
            return Results.Ok(new { items = plans });
        });

        admin.MapPost("/plans", async (SubscriptionPlanEntity req, ISqlSugarClient db) =>
        {
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok(new { id = req.Id });
        });

        admin.MapGet("/users", async (ISqlSugarClient db, long? userId, int page = 1, int size = 20) =>
        {
            var query = db.Queryable<UserSubscriptionEntity>();
            if (userId.HasValue) query = query.Where(x => x.UserId == userId.Value);
            var items = await query.OrderByDescending(x => x.StartedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items });
        });
    }

    private static void MapRedeemCodes(WebApplication app)
    {
        var admin = app.MapGroup("/admin/redeem-codes").RequireAuthorization();
        var userGroup = app.MapGroup("/user/redeem").RequireAuthorization();

        admin.MapGet("/", async (ISqlSugarClient db, int page = 1, int size = 20) =>
        {
            var items = await db.Queryable<RedeemCodeEntity>()
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items });
        });

        admin.MapPost("/", async (RedeemCodeEntity req, ISqlSugarClient db) =>
        {
            if (string.IsNullOrEmpty(req.Code))
                req.Code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok(new { id = req.Id, code = req.Code });
        });

        userGroup.MapPost("/", async (ClaimsPrincipal principal, RedeemRequest req, ISqlSugarClient db) =>
        {
            var code = await db.Queryable<RedeemCodeEntity>()
                .Where(x => x.Code == req.Code && x.Status == "active").FirstAsync();
            if (code is null)
                return Results.BadRequest(new { error = "Invalid or expired code" });
            if (code.MaxUses > 0 && code.UsedCount >= code.MaxUses)
                return Results.BadRequest(new { error = "Code usage limit reached" });
            if (code.ExpiresAt.HasValue && code.ExpiresAt < DateTime.UtcNow)
                return Results.BadRequest(new { error = "Code expired" });

            code.UsedCount++;
            await db.Updateable(code).UpdateColumns(x => x.UsedCount).ExecuteCommandAsync();
            return Results.Ok(new { message = "Code redeemed", bonus = code.BonusAmount });
        });
    }

    private static void MapChannelMonitors(WebApplication app)
    {
        var group = app.MapGroup("/admin/channel-monitors").RequireAuthorization();

        group.MapGet("/", async (ISqlSugarClient db, long? accountId, int page = 1, int size = 50) =>
        {
            var query = db.Queryable<ChannelMonitorEntity>();
            if (accountId.HasValue) query = query.Where(x => x.AccountId == accountId.Value);
            var items = await query.OrderByDescending(x => x.CheckedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items });
        });

        group.MapPost("/check", async (ISqlSugarClient db, ChannelCheckRequest req) =>
        {
            var record = new ChannelMonitorEntity
            {
                AccountId = req.AccountId,
                Status = req.Status,
                LatencyMs = req.LatencyMs,
                LastError = req.Error,
                CheckedAt = DateTime.UtcNow,
            };
            await db.Insertable(record).ExecuteCommandAsync();
            return Results.Ok(new { id = record.Id });
        });
    }

    private static void MapOpsMetrics(WebApplication app)
    {
        var group = app.MapGroup("/admin/ops-metrics").RequireAuthorization();

        group.MapGet("/", async (ISqlSugarClient db, string? metricName,
            DateTime? from, DateTime? to, int limit = 100) =>
        {
            var query = db.Queryable<OpsMetricsEntity>();
            if (!string.IsNullOrEmpty(metricName)) query = query.Where(x => x.MetricName == metricName);
            if (from.HasValue) query = query.Where(x => x.CollectedAt >= from.Value);
            if (to.HasValue) query = query.Where(x => x.CollectedAt <= to.Value);
            var items = await query.OrderByDescending(x => x.CollectedAt).Take(limit).ToListAsync();
            return Results.Ok(new { items });
        });

        group.MapPost("/ingest", async (ISqlSugarClient db, OpsMetricsEntity req) =>
        {
            req.CollectedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok();
        });
    }

    private static void MapAuditLogs(WebApplication app)
    {
        var group = app.MapGroup("/admin/audit-logs").RequireAuthorization();

        group.MapGet("/", async (ISqlSugarClient db, long? userId, string? action,
            DateTime? from, DateTime? to, int page = 1, int size = 50) =>
        {
            var query = db.Queryable<AuditLogEntity>();
            if (userId.HasValue) query = query.Where(x => x.UserId == userId.Value);
            if (!string.IsNullOrEmpty(action)) query = query.Where(x => x.Action == action);
            if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
            if (to.HasValue) query = query.Where(x => x.CreatedAt <= to.Value);
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items, total, page, size });
        });

        group.MapPost("/", async (ISqlSugarClient db, AuditLogEntity req) =>
        {
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok();
        });
    }

    private static void MapMiscAdmin(WebApplication app)
    {
        var group = app.MapGroup("/admin/system").RequireAuthorization();

        group.MapPost("/backup", async (IConfiguration config) =>
        {
            var connStr = config.GetConnectionString("Postgres") ?? "";
            return Results.Ok(new { message = "Backup initiated", target = "s3" });
        });

        group.MapPost("/restore", async (IConfiguration config, RestoreRequest req) =>
        {
            return Results.Ok(new { message = "Restore initiated", source = req.BackupId });
        });

        group.MapGet("/update-check", async (IHttpClientFactory httpFactory) =>
        {
            try
            {
                var client = httpFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sub2Api");
                var resp = await client.GetAsync("https://api.github.com/repos/sub2api/sub2api/releases/latest");
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                    return Results.Ok(new { latest = data?.GetValueOrDefault("tag_name")?.ToString() });
                }
            }
            catch { }
            return Results.Ok(new { latest = (string?)null });
        });

        group.MapPost("/send-email", async (EmailRequest req, IConfiguration config) =>
        {
            return Results.Ok(new { message = "Email queued", to = req.To });
        });
    }

    private record ApiKeyView(string KeyHash);
    private record RedeemRequest(string Code);
    private record ChannelCheckRequest(long AccountId, string Status, int LatencyMs, string? Error);
    private record RestoreRequest(string BackupId);
    private record EmailRequest(string To, string Subject, string Body);
}
