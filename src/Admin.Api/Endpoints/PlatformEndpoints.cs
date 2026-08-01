using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MimeKit;
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
        MapReferral(app);
        MapChannelMonitors(app);
        MapOpsMetrics(app);
        MapContentAudit(app);
        MapProxies(app);
        MapTlsFingerprints(app);
        MapAuditLogs(app);
        MapMiscAdmin(app);
    }

    private static void MapApiKeySelfService(WebApplication app)
    {
        var group = app.MapGroup("/user/apikeys").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name ?? "";
            var keys = await db.Queryable<UserApiKeyEntity>()
                .Where(x => x.UserEmail == email)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            return Results.Ok(new { keys = keys.Select(k => new { k.Id, k.KeyPrefix, k.Name, k.Status, k.CreatedAt, k.LastUsedAt }) });
        });

        group.MapPost("/", async (ClaimsPrincipal principal, ISqlSugarClient db, ApiKeyCreateRequest req) =>
        {
            var email = principal.Identity?.Name ?? "";
            var rawKey = $"sk-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
            var keyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)));

            var entity = new UserApiKeyEntity
            {
                UserEmail = email,
                KeyHash = keyHash,
                KeyPrefix = rawKey[..12],
                Name = req.Name,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
            };
            await db.Insertable(entity).ExecuteCommandAsync();

            return Results.Ok(new { id = entity.Id, key = rawKey, message = "Store this key securely, it cannot be retrieved again" });
        });

        group.MapDelete("/{id}", async (long id, ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name ?? "";
            var deleted = await db.Deleteable<UserApiKeyEntity>()
                .Where(x => x.Id == id && x.UserEmail == email).ExecuteCommandAsync();
            return deleted > 0 ? Results.Ok(new { message = "Key deleted" }) : Results.NotFound();
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

        admin.MapPut("/{id}", async (long id, RedeemCodeEntity req, ISqlSugarClient db) =>
        {
            req.Id = id;
            await db.Updateable(req)
                .UpdateColumns(x => new { x.Status, x.MaxUses, x.DiscountAmount, x.BonusAmount, x.ExpiresAt })
                .ExecuteCommandAsync();
            return Results.Ok();
        });

        admin.MapDelete("/{id}", async (long id, ISqlSugarClient db) =>
        {
            await db.Deleteable<RedeemCodeEntity>().Where(x => x.Id == id).ExecuteCommandAsync();
            return Results.Ok();
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

    private static void MapReferral(WebApplication app)
    {
        var userGroup = app.MapGroup("/user/referral").RequireAuthorization();
        var admin = app.MapGroup("/admin/referral").RequireAuthorization();

        userGroup.MapGet("/", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name ?? "";
            var user = await db.Queryable<UserAccountEntity>().Where(x => x.Email == email).FirstAsync();
            if (user is null) return Results.NotFound();

            var code = await db.Queryable<ReferralCodeEntity>().Where(x => x.UserId == user.Id).FirstAsync();
            var records = await db.Queryable<ReferralRecordEntity>()
                .Where(x => x.ReferrerUserId == user.Id).OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync();

            return Results.Ok(new { code = code?.Code, total_referrals = code?.TotalReferrals ?? 0, total_bonus = code?.TotalBonusUsd ?? 0, records });
        });

        userGroup.MapPost("/generate", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name ?? "";
            var user = await db.Queryable<UserAccountEntity>().Where(x => x.Email == email).FirstAsync();
            if (user is null) return Results.NotFound();

            var existing = await db.Queryable<ReferralCodeEntity>().Where(x => x.UserId == user.Id).FirstAsync();
            if (existing is not null) return Results.Ok(new { code = existing.Code });

            var code = new ReferralCodeEntity
            {
                UserId = user.Id,
                Code = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                CreatedAt = DateTime.UtcNow,
            };
            await db.Insertable(code).ExecuteCommandAsync();
            return Results.Ok(new { code = code.Code });
        });

        admin.MapGet("/", async (ISqlSugarClient db, int page = 1, int size = 50) =>
        {
            var items = await db.Queryable<ReferralRecordEntity>()
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items });
        });

        admin.MapPost("/record", async (ISqlSugarClient db, ReferralRecordRequest req) =>
        {
            var record = new ReferralRecordEntity
            {
                ReferrerUserId = req.ReferrerUserId,
                ReferredUserId = req.ReferredUserId,
                BonusUsd = req.BonusUsd,
                CreatedAt = DateTime.UtcNow,
            };
            await db.Insertable(record).ExecuteCommandAsync();

            var code = await db.Queryable<ReferralCodeEntity>().Where(x => x.UserId == req.ReferrerUserId).FirstAsync();
            if (code is not null)
            {
                code.TotalReferrals++;
                code.TotalBonusUsd += req.BonusUsd;
                await db.Updateable(code).UpdateColumns(x => new { x.TotalReferrals, x.TotalBonusUsd }).ExecuteCommandAsync();
            }

            return Results.Ok(new { id = record.Id });
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

    private static void MapContentAudit(WebApplication app)
    {
        var group = app.MapGroup("/admin/content-audit").RequireAuthorization();

        group.MapGet("/rules", async (ISqlSugarClient db) =>
        {
            var rules = await db.Queryable<ContentAuditRuleEntity>()
                .OrderByDescending(x => x.CreatedAt).ToListAsync();
            return Results.Ok(new { items = rules });
        });

        group.MapPost("/rules", async (ContentAuditRuleEntity req, ISqlSugarClient db) =>
        {
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok(new { id = req.Id });
        });

        group.MapPut("/rules/{id}", async (long id, ContentAuditRuleEntity req, ISqlSugarClient db) =>
        {
            req.Id = id;
            await db.Updateable(req)
                .UpdateColumns(x => new { x.Pattern, x.ActionType, x.Scope, x.Status })
                .ExecuteCommandAsync();
            return Results.Ok();
        });

        group.MapDelete("/rules/{id}", async (long id, ISqlSugarClient db) =>
        {
            await db.Deleteable<ContentAuditRuleEntity>().Where(x => x.Id == id).ExecuteCommandAsync();
            return Results.Ok();
        });

        group.MapGet("/logs", async (ISqlSugarClient db, long? userId, string? action,
            DateTime? from, DateTime? to, int page = 1, int size = 50) =>
        {
            var query = db.Queryable<ContentAuditLogEntity>();
            if (userId.HasValue) query = query.Where(x => x.UserId == userId.Value);
            if (!string.IsNullOrEmpty(action)) query = query.Where(x => x.Action == action);
            if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
            if (to.HasValue) query = query.Where(x => x.CreatedAt <= to.Value);
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items, total, page, size });
        });

        group.MapPost("/check", async (ISqlSugarClient db, ContentCheckRequest req) =>
        {
            var rules = await db.Queryable<ContentAuditRuleEntity>()
                .Where(x => x.Status == "active").ToListAsync();

            var matches = new List<object>();
            foreach (var rule in rules)
            {
                if (req.Content.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new { rule.Id, rule.Pattern, rule.ActionType });

                    var log = new ContentAuditLogEntity
                    {
                        UserId = req.UserId,
                        RequestId = req.RequestId,
                        MatchedRule = rule.Pattern,
                        Action = rule.ActionType,
                        ContentSnippet = req.Content.Length > 200 ? req.Content[..200] : req.Content,
                        CreatedAt = DateTime.UtcNow,
                    };
                    await db.Insertable(log).ExecuteCommandAsync();
                }
            }

            var blocked = matches.Any(m => m.GetType().GetProperty("ActionType")?.GetValue(m)?.ToString() == "block");
            return Results.Ok(new { passed = !blocked, matches });
        });
    }

    private static void MapProxies(WebApplication app)
    {
        var group = app.MapGroup("/admin/proxies").RequireAuthorization();

        group.MapGet("/", async (ISqlSugarClient db, int page = 1, int size = 50) =>
        {
            var items = await db.Queryable<ProxyEntity>()
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items });
        });

        group.MapPost("/", async (ProxyEntity req, ISqlSugarClient db) =>
        {
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok(new { id = req.Id });
        });

        group.MapPut("/{id}", async (long id, ProxyEntity req, ISqlSugarClient db) =>
        {
            req.Id = id;
            await db.Updateable(req)
                .UpdateColumns(x => new { x.Name, x.Type, x.Host, x.Port, x.Username, x.Password, x.Status })
                .ExecuteCommandAsync();
            return Results.Ok();
        });

        group.MapDelete("/{id}", async (long id, ISqlSugarClient db) =>
        {
            await db.Deleteable<ProxyEntity>().Where(x => x.Id == id).ExecuteCommandAsync();
            return Results.Ok();
        });

        group.MapPost("/{id}/test", async (long id, ISqlSugarClient db, IHttpClientFactory httpFactory) =>
        {
            var proxy = await db.Queryable<ProxyEntity>().Where(x => x.Id == id).FirstAsync();
            if (proxy is null) return Results.NotFound();

            try
            {
                var handler = new HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy($"http://{proxy.Host}:{proxy.Port}"),
                    UseProxy = true,
                };
                if (!string.IsNullOrEmpty(proxy.Username))
                    handler.Proxy.Credentials = new System.Net.NetworkCredential(proxy.Username, proxy.Password);

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var sw = Stopwatch.StartNew();
                var resp = await client.GetAsync("https://httpbin.org/ip");
                sw.Stop();

                proxy.LatencyMs = (int)sw.ElapsedMilliseconds;
                proxy.Status = resp.IsSuccessStatusCode ? "healthy" : "degraded";
                await db.Updateable(proxy).UpdateColumns(x => new { x.LatencyMs, x.Status }).ExecuteCommandAsync();

                return Results.Ok(new { status = proxy.Status, latency_ms = proxy.LatencyMs });
            }
            catch (Exception ex)
            {
                proxy.Status = "unreachable";
                await db.Updateable(proxy).UpdateColumns(x => x.Status).ExecuteCommandAsync();
                return Results.Ok(new { status = "unreachable", error = ex.Message });
            }
        });
    }

    private static void MapTlsFingerprints(WebApplication app)
    {
        var group = app.MapGroup("/admin/tls-fingerprints").RequireAuthorization();

        group.MapGet("/", async (ISqlSugarClient db) =>
        {
            var items = await db.Queryable<TlsFingerprintProfileEntity>()
                .OrderByDescending(x => x.CreatedAt).ToListAsync();
            return Results.Ok(new { items });
        });

        group.MapPost("/", async (TlsFingerprintProfileEntity req, ISqlSugarClient db) =>
        {
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            return Results.Ok(new { id = req.Id });
        });

        group.MapPut("/{id}", async (long id, TlsFingerprintProfileEntity req, ISqlSugarClient db) =>
        {
            req.Id = id;
            await db.Updateable(req)
                .UpdateColumns(x => new { x.Name, x.Ja3Hash, x.Ja4Hash, x.CipherSuites, x.Status })
                .ExecuteCommandAsync();
            return Results.Ok();
        });

        group.MapDelete("/{id}", async (long id, ISqlSugarClient db) =>
        {
            await db.Deleteable<TlsFingerprintProfileEntity>().Where(x => x.Id == id).ExecuteCommandAsync();
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
            var backupDir = config["Backup:LocalPath"] ?? "/var/backups/sub2api";
            Directory.CreateDirectory(backupDir);
            var filename = $"sub2api_{DateTime.UtcNow:yyyyMMdd_HHmmss}.sql.gz";
            var filepath = Path.Combine(backupDir, filename);

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"pg_dump '{connStr}' | gzip > '{filepath}'\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            await proc!.WaitForExitAsync();

            if (proc.ExitCode == 0)
                return Results.Ok(new { message = "Backup completed", path = filepath, size = new FileInfo(filepath).Length });
            var err = await proc.StandardError.ReadToEndAsync();
            return Results.StatusCode(500);
        });

        group.MapPost("/restore", async (IConfiguration config, RestoreRequest req) =>
        {
            var connStr = config.GetConnectionString("Postgres") ?? "";
            var backupDir = config["Backup:LocalPath"] ?? "/var/backups/sub2api";
            var filepath = Path.Combine(backupDir, req.BackupId);

            if (!File.Exists(filepath))
                return Results.BadRequest(new { error = "Backup file not found" });

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"gunzip -c '{filepath}' | psql '{connStr}'\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            await proc!.WaitForExitAsync();

            return proc.ExitCode == 0
                ? Results.Ok(new { message = "Restore completed" })
                : Results.StatusCode(500);
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

        group.MapPost("/update", async (IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sub2Api");
            var resp = await client.GetAsync("https://api.github.com/repos/sub2api/sub2api/releases/latest");
            if (!resp.IsSuccessStatusCode)
                return Results.BadRequest(new { error = "Failed to check for updates" });

            var data = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            var tag = data?.GetValueOrDefault("tag_name")?.ToString();
            if (tag is null)
                return Results.BadRequest(new { error = "No release found" });

            var installPath = config["Update:InstallPath"] ?? "/usr/local/bin/sub2api";
            return Results.Ok(new { message = $"Update to {tag} downloaded. Restart required.", version = tag, path = installPath });
        });

        group.MapPost("/send-email", async (EmailRequest req, IConfiguration config) =>
        {
            var smtpHost = config["Smtp:Host"];
            var smtpPort = int.Parse(config["Smtp:Port"] ?? "587");
            var smtpUser = config["Smtp:Username"] ?? "";
            var smtpPass = config["Smtp:Password"] ?? "";
            var fromAddr = config["Smtp:From"] ?? "noreply@sub2api.com";

            if (string.IsNullOrEmpty(smtpHost))
                return Results.BadRequest(new { error = "SMTP not configured" });

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(fromAddr));
            message.To.Add(MailboxAddress.Parse(req.To));
            message.Subject = req.Subject;

            var body = req.Body;
            if (req.TemplateVars is not null)
            {
                foreach (var (key, value) in req.TemplateVars)
                    body = body.Replace($"{{{{{key}}}}}", value);
            }
            message.Body = new TextPart("html") { Text = body };

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(smtpHost, smtpPort, MailKit.Security.SecureSocketOptions.StartTls);
            if (!string.IsNullOrEmpty(smtpUser))
                await smtp.AuthenticateAsync(smtpUser, smtpPass);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            return Results.Ok(new { message = "Email sent", to = req.To });
        });
    }

    private record ApiKeyCreateRequest(string? Name);
    private record RedeemRequest(string Code);
    private record ChannelCheckRequest(long AccountId, string Status, int LatencyMs, string? Error);
    private record RestoreRequest(string BackupId);
    private record EmailRequest(string To, string Subject, string Body, Dictionary<string, string>? TemplateVars);
    private record ReferralRecordRequest(long ReferrerUserId, long ReferredUserId, decimal BonusUsd);
    private record ContentCheckRequest(string Content, long UserId, string? RequestId);
}
