using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MimeKit;
using Npgsql;
using SqlSugar;
using ScalaAPI.Data.Entities;
using ScalaAPI.Admin.Data;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Data.Repositories;
using Orleans;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public record SubscriptionPurchaseRequest(long PlanId, string? ExternalReference, bool AutoRenew = true);
public record SubscriptionRenewRequest(bool AutoRenew = true);
public record PricingVersionRequest(
    string Version, string Model, decimal InputUsdPerMillion, decimal OutputUsdPerMillion,
    decimal CacheReadUsdPerMillion, decimal CacheWriteUsdPerMillion,
    DateTime EffectiveFrom, DateTime? EffectiveUntil);

public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this WebApplication app)
    {
        app.MapPaymentWebhookEndpoints();
        MapApiKeySelfService(app);
        MapUserUsage(app);
        MapUsageSummary(app);
        MapAnnouncements(app);
        MapPayments(app);
        MapSubscriptions(app);
        MapPricing(app);
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
        var group = app.MapGroup("/user/apikeys").RequireAuthorization("UserOnly");

        group.MapGet("/", async (ClaimsPrincipal principal, ISqlSugarClient db) =>
        {
            var email = principal.Identity?.Name ?? "";
            var keys = await db.Queryable<UserApiKeyEntity>()
                .Where(x => x.UserEmail == email)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            return Results.Ok(new { keys = keys.Select(k => new
            {
                k.Id, k.KeyPrefix, k.Name, k.Status, k.CreatedAt, k.LastUsedAt,
                scopes = ParseScopes(k.Scopes), expires_at = k.ExpiresAtMs,
            }) });
        });

        group.MapPost("/", async (ClaimsPrincipal principal, ISqlSugarClient db,
            IClusterClient client, ListingRepository registry, ApiKeyAuditStore audit,
            ApiKeySelfServiceRequest req) =>
        {
            var email = principal.Identity?.Name ?? "";
            var user = await db.Queryable<UserAccountEntity>().Where(x => x.Email == email).FirstAsync();
            if (user is null) return Results.Unauthorized();
            if (!ScalaAPI.Admin.Auth.AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            string[] scopes;
            try { scopes = ApiKeyScopes.Normalize(req.Scopes); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (!IsFutureExpiry(req.ExpiresAt))
                return Results.BadRequest(new { error = "expires_at must be in the future" });
            var userGrain = client.GetGrain<IUserGrain>(user.Id);
            var projection = await userGrain.GetAuthProjection();
            long? groupId = req.GroupId ?? projection.AllowedGroups.FirstOrDefault();
            if (groupId is null or <= 0 || (projection.AllowedGroups.Length > 0 && !projection.AllowedGroups.Contains(groupId.Value)))
                return Results.BadRequest(new { error = "No permitted group selected" });

            var rawKey = $"sk-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
            var keyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)))
                .ToLowerInvariant();
            var allocator = client.GetGrain<IIdAllocatorGrain>("apiKey");
            var apiKeyId = await allocator.Next();
            var grain = client.GetGrain<IApiKeyGrain>(keyHash);
            await grain.Create(new ApiKeyUpsert(
                user.Id, groupId.Value, req.Quota ?? 0, req.ExpiresAt,
                req.IpWhitelist ?? [], req.IpBlacklist ?? [],
                req.RateLimit5h ?? 0, req.RateLimit1d ?? 0, req.RateLimit7d ?? 0, scopes), apiKeyId);

            var entity = new UserApiKeyEntity
            {
                UserEmail = email,
                KeyHash = keyHash,
                KeyPrefix = rawKey[..12],
                Name = req.Name,
                ApiKeyId = apiKeyId,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                Scopes = JsonSerializer.Serialize(scopes),
                ExpiresAtMs = req.ExpiresAt,
            };
            await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO user_api_keys
                    (api_key_id, user_email, key_hash, key_prefix, name, status, created_at, scopes, expires_at_ms)
                VALUES (@apiKeyId, @email, @hash, @prefix, @name::text, 'active', @created, @scopes::jsonb, @expires::bigint)
                """,
                new SugarParameter("@apiKeyId", entity.ApiKeyId),
                new SugarParameter("@email", entity.UserEmail),
                new SugarParameter("@hash", entity.KeyHash),
                new SugarParameter("@prefix", entity.KeyPrefix),
                new SugarParameter("@name", (object?)entity.Name ?? DBNull.Value),
                new SugarParameter("@created", entity.CreatedAt),
                new SugarParameter("@scopes", entity.Scopes),
                new SugarParameter("@expires", (object?)entity.ExpiresAtMs ?? DBNull.Value));
            entity.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
                "SELECT id FROM user_api_keys WHERE key_hash = @hash",
                new SugarParameter("@hash", keyHash)));
            await registry.RegisterString("apiKey", keyHash, apiKeyId);
            await audit.RecordAsync(apiKeyId, user.Id, actorId, "created", scopes, req.ExpiresAt);

            return Results.Ok(new { id = entity.Id, key = rawKey, message = "Store this key securely, it cannot be retrieved again" });
        });

        group.MapDelete("/{id}", async (long id, ClaimsPrincipal principal, ISqlSugarClient db,
            IClusterClient client, ListingRepository registry, ApiKeyAuditStore audit) =>
        {
            var email = principal.Identity?.Name ?? "";
            var key = await db.Queryable<UserApiKeyEntity>()
                .Where(x => x.Id == id && x.UserEmail == email).FirstAsync();
            if (key is null) return Results.NotFound();
            if (!ScalaAPI.Admin.Auth.AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            var config = await client.GetGrain<IApiKeyGrain>(key.KeyHash).GetConfig();
            await client.GetGrain<IApiKeyGrain>(key.KeyHash).Revoke();
            await db.Updateable<UserApiKeyEntity>().SetColumns(x => x.Status == "revoked")
                .Where(x => x.Id == id && x.UserEmail == email).ExecuteCommandAsync();
            await registry.Unregister("apiKey", key.KeyHash);
            await audit.RecordAsync(key.ApiKeyId, config.UserId, actorId, "revoked",
                config.Scopes, config.ExpiresAt, reason: "user revoke");
            return Results.Ok(new { message = "Key revoked" });
        });

        group.MapPost("/{id:long}/rotate", async (long id, ClaimsPrincipal principal,
            ISqlSugarClient db, IClusterClient client, ListingRepository registry,
            ApiKeyAuditStore audit) =>
        {
            var email = principal.Identity?.Name ?? "";
            var old = await db.Queryable<UserApiKeyEntity>()
                .Where(x => x.Id == id && x.UserEmail == email && x.Status == "active")
                .FirstAsync();
            if (old is null) return Results.NotFound();
            if (!ScalaAPI.Admin.Auth.AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();

            var config = await client.GetGrain<IApiKeyGrain>(old.KeyHash).GetConfig();
            var rawKey = $"sk-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
            var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)))
                .ToLowerInvariant();
            var allocator = client.GetGrain<IIdAllocatorGrain>("apiKey");
            var apiKeyId = await allocator.Next();
            await client.GetGrain<IApiKeyGrain>(keyHash).Create(new ApiKeyUpsert(
                config.UserId, config.GroupId, config.Quota, config.ExpiresAt,
                config.IpWhitelist, config.IpBlacklist,
                config.RateLimit5h, config.RateLimit1d, config.RateLimit7d, config.Scopes), apiKeyId);

            var replacement = new UserApiKeyEntity
            {
                UserEmail = email,
                KeyHash = keyHash,
                KeyPrefix = rawKey[..12],
                Name = old.Name,
                ApiKeyId = apiKeyId,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                Scopes = JsonSerializer.Serialize(config.Scopes),
                ExpiresAtMs = config.ExpiresAt,
            };
            await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO user_api_keys
                    (api_key_id, user_email, key_hash, key_prefix, name, status, created_at, scopes, expires_at_ms)
                VALUES (@apiKeyId, @email, @hash, @prefix, @name::text, 'active', @created, @scopes::jsonb, @expires::bigint)
                """,
                new SugarParameter("@apiKeyId", replacement.ApiKeyId),
                new SugarParameter("@email", replacement.UserEmail),
                new SugarParameter("@hash", replacement.KeyHash),
                new SugarParameter("@prefix", replacement.KeyPrefix),
                new SugarParameter("@name", (object?)replacement.Name ?? DBNull.Value),
                new SugarParameter("@created", replacement.CreatedAt),
                new SugarParameter("@scopes", replacement.Scopes),
                new SugarParameter("@expires", (object?)replacement.ExpiresAtMs ?? DBNull.Value));
            replacement.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
                "SELECT id FROM user_api_keys WHERE key_hash = @hash",
                new SugarParameter("@hash", keyHash)));
            await registry.RegisterString("apiKey", keyHash, apiKeyId);

            await client.GetGrain<IApiKeyGrain>(old.KeyHash).Revoke();
            await db.Updateable<UserApiKeyEntity>().SetColumns(x => x.Status == "revoked")
                .Where(x => x.Id == old.Id && x.UserEmail == email).ExecuteCommandAsync();
            await registry.Unregister("apiKey", old.KeyHash);
            await audit.RecordAsync(apiKeyId, config.UserId, actorId, "rotated",
                config.Scopes, config.ExpiresAt, reason: "user rotation");
            await audit.RecordAsync(old.ApiKeyId, config.UserId, actorId, "revoked",
                config.Scopes, config.ExpiresAt, reason: "rotation replaced previous key");

            return Results.Ok(new
            {
                id = replacement.Id, key = rawKey,
                message = "Store this key securely, it cannot be retrieved again",
            });
        });
    }

    private static void MapUsageSummary(WebApplication app)
    {
        var group = app.MapGroup("/admin/usage/summary").RequireAuthorization("AdminOnly");

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

    private static void MapUserUsage(WebApplication app)
    {
        var group = app.MapGroup("/user/usage").RequireAuthorization("UserOnly");

        group.MapGet("/", async (ClaimsPrincipal principal, ISqlSugarClient db,
            IUsageLogRepository usage, string? model, DateTime? from, DateTime? to,
            int page = 1, int size = 50) =>
        {
            var email = principal.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
            var account = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email && x.Status == "active").FirstAsync();
            if (account is null) return Results.Unauthorized();

            page = Math.Max(page, 1);
            size = Math.Clamp(size, 1, 100);
            var items = await usage.GetPaged(account.Id, model, from, to, page, size);
            var total = await usage.Count(account.Id, model, from, to);
            return Results.Ok(new
            {
                items = items.Select(item => new
                {
                    request_id = item.RequestId,
                    model = item.Model,
                    input_tokens = item.InputTokens,
                    output_tokens = item.OutputTokens,
                    cost_usd = item.CostUsd,
                    duration_ms = item.DurationMs,
                    stream = item.Stream,
                    client_disconnect = item.ClientDisconnect,
                    created_at = item.CreatedAt,
                }),
                total,
                page,
                size,
                pages = (int)Math.Ceiling((double)total / size),
            });
        });

        group.MapGet("/balance", async (ClaimsPrincipal principal,
            ISqlSugarClient db, AccountingStore accounting, CancellationToken ct) =>
        {
            var email = principal.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
            var user = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Email == email && x.Status == "active").FirstAsync();
            if (user is null) return Results.Unauthorized();
            var snapshot = await accounting.GetSnapshotAsync(user.Id, ct);
            return Results.Ok(new
            {
                user_id = snapshot.UserId,
                balance = snapshot.Balance,
                ledger_version = snapshot.Version,
            });
        });
    }

    private static void MapAnnouncements(WebApplication app)
    {
        var admin = app.MapGroup("/admin/announcements").RequireAuthorization("AdminOnly");
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
        var group = app.MapGroup("/admin/payments").RequireAuthorization("AdminOnly");
        var userGroup = app.MapGroup("/user/payments").RequireAuthorization("UserOnly");

        userGroup.MapPost("/create", async (ClaimsPrincipal principal, PaymentOrderEntity req,
            ISqlSugarClient db, HttpRequest http) =>
        {
            var email = principal.Identity?.Name ?? "";
            var user = await db.Queryable<UserAccountEntity>().Where(x => x.Email == email).FirstAsync();
            if (user is null) return Results.Unauthorized();
            if (req.Amount <= 0 || req.Amount > 1_000_000m)
                return Results.BadRequest(new { error = "Invalid payment amount" });
            var idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
                return Results.BadRequest(new { error = "Idempotency-Key is required" });
            var existing = await db.Queryable<PaymentOrderEntity>()
                .Where(x => x.UserId == user.Id && x.IdempotencyKey == idempotencyKey).FirstAsync();
            if (existing is not null)
                return Results.Ok(new { id = existing.Id, status = existing.Status, duplicate = true });

            req.UserId = user.Id;
            req.IdempotencyKey = idempotencyKey;
            req.Status = "pending";
            req.CreatedAt = DateTime.UtcNow;
            await db.Insertable(req).ExecuteCommandAsync();
            req.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
                "SELECT id FROM payment_orders WHERE user_id = @user_id AND idempotency_key = @key",
                new SugarParameter("@user_id", user.Id),
                new SugarParameter("@key", idempotencyKey)));
            return Results.Ok(new { id = req.Id, status = req.Status });
        });

        userGroup.MapGet("/", async (ClaimsPrincipal principal, ISqlSugarClient db, int page = 1, int size = 20) =>
        {
            var email = principal.Identity?.Name ?? "";
            var user = await db.Queryable<UserAccountEntity>().Where(x => x.Email == email).FirstAsync();
            if (user is null) return Results.Unauthorized();
            page = Math.Max(page, 1);
            size = Math.Clamp(size, 1, 100);
            var items = await db.Queryable<PaymentOrderEntity>()
                .Where(x => x.UserId == user.Id)
                .OrderByDescending(x => x.CreatedAt)
                .Skip((page - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items });
        });

        group.MapPost("/{id}/confirm", async (long id, NpgsqlDataSource dataSource,
            AccountingStore accounting, AccountingProjectionService projection,
            CancellationToken ct) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            long userId;
            decimal amount;
            string status;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT user_id, amount, status FROM payment_orders WHERE id = $1 FOR UPDATE";
                find.Parameters.AddWithValue(id);
                await using var reader = await find.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                userId = reader.GetInt64(0);
                amount = reader.GetDecimal(1);
                status = reader.GetString(2);
            }
            if (status is not ("pending" or "paid"))
                return Results.Conflict(new { error = "Only a pending or paid payment can be confirmed" });

            if (status == "pending")
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE payment_orders SET status = 'paid', paid_at = now() WHERE id = $1";
                update.Parameters.AddWithValue(id);
                await update.ExecuteNonQueryAsync(ct);
            }
            var effect = await accounting.AppendEffectAsync(connection, transaction,
                new AccountingEffect(userId, $"payment:{id}", "payment_credit", amount,
                    PaymentId: id), ct);
            if (effect.Status == AccountingEffectStatus.Conflict)
                return Results.Conflict(new { error = "Payment accounting effect changed" });
            await transaction.CommitAsync(ct);
            await projection.ApplyAsync(effect.Snapshot, ct);
            return Results.Ok(new
            {
                message = "Payment confirmed",
                duplicate = effect.Status == AccountingEffectStatus.Replay,
                ledger_version = effect.Snapshot.Version,
                balance = effect.Snapshot.Balance,
            });
        });
    }

    private static void MapSubscriptions(WebApplication app)
    {
        var admin = app.MapGroup("/admin/subscriptions").RequireAuthorization("AdminOnly");
        var user = app.MapGroup("/user/subscriptions").RequireAuthorization("UserOnly");

        admin.MapGet("/plans", async (ISqlSugarClient db) =>
        {
            var plans = await db.Queryable<SubscriptionPlanEntity>().ToListAsync();
            return Results.Ok(new { items = plans });
        });

        admin.MapPost("/plans", async (SubscriptionPlanEntity req, ISqlSugarClient db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.PriceMonthly < 0 || req.QuotaUsd < 0)
                return Results.BadRequest(new { error = "Invalid subscription plan" });
            await db.Insertable(req).ExecuteCommandAsync();
            req.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
                "SELECT id FROM subscription_plans WHERE name = @name AND price_monthly = @price ORDER BY id DESC LIMIT 1",
                new SugarParameter("@name", req.Name),
                new SugarParameter("@price", req.PriceMonthly)));
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

        user.MapGet("/", async (ClaimsPrincipal principal, NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var userId = await FindUserIdAsync(connection, principal.Identity?.Name, ct);
            if (userId is null) return Results.Unauthorized();
            await ExpireSubscriptionsAsync(connection, userId.Value, ct);
            var items = await ReadSubscriptionsAsync(connection, userId.Value, ct);
            return Results.Ok(new { items });
        });

        user.MapGet("/plans", async (NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            await using var command = dataSource.CreateCommand("""
                SELECT id, name, price_monthly, quota_usd, status
                FROM subscription_plans
                WHERE status = 'active'
                ORDER BY price_monthly, id
                """);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<object>();
            while (await reader.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = reader.GetInt64(0),
                    name = reader.GetString(1),
                    priceMonthly = reader.GetDecimal(2),
                    quotaUsd = reader.GetDecimal(3),
                    interval = "month",
                    status = reader.GetString(4),
                });
            }
            return Results.Ok(new { items });
        });

        user.MapPost("/", async (ClaimsPrincipal principal, SubscriptionPurchaseRequest req,
            NpgsqlDataSource dataSource, HttpRequest http, CancellationToken ct) =>
        {
            if (req.PlanId <= 0) return Results.BadRequest(new { error = "PlanId is required" });
            var idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
                return Results.BadRequest(new { error = "Idempotency-Key is required" });

            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var userId = await FindUserIdAsync(connection, principal.Identity?.Name, ct);
            if (userId is null) return Results.Unauthorized();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await ExpireSubscriptionsAsync(connection, userId.Value, ct, transaction);

            var existingId = await ScalarLongAsync(connection, transaction,
                "SELECT id FROM user_subscriptions WHERE user_id = $1 AND idempotency_key = $2 FOR UPDATE",
                ct, userId.Value, idempotencyKey);
            if (existingId is not null)
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new { id = existingId.Value, duplicate = true, status = "active" });
            }

            var activeId = await ScalarLongAsync(connection, transaction,
                "SELECT id FROM user_subscriptions WHERE user_id = $1 AND status = 'active' FOR UPDATE",
                ct, userId.Value);
            if (activeId is not null)
                return Results.Conflict(new { error = "An active subscription already exists", id = activeId.Value });

            await using var plan = connection.CreateCommand();
            plan.Transaction = transaction;
            plan.CommandText = "SELECT quota_usd FROM subscription_plans WHERE id = $1 AND status = 'active' FOR SHARE";
            plan.Parameters.AddWithValue(req.PlanId);
            var quota = await plan.ExecuteScalarAsync(ct);
            if (quota is null || quota is DBNull)
                return Results.NotFound(new { error = "Subscription plan not found" });

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO user_subscriptions
                    (user_id, plan_id, status, started_at, expires_at, renewal_at,
                     provider, external_reference, idempotency_key, quota_granted_usd)
                VALUES ($1, $2, 'active', now(), now() + interval '30 days',
                        CASE WHEN $3 THEN now() + interval '30 days' ELSE NULL END,
                        'internal', $4, $5, $6)
                RETURNING id
                """;
            insert.Parameters.AddWithValue(userId.Value);
            insert.Parameters.AddWithValue(req.PlanId);
            insert.Parameters.AddWithValue(req.AutoRenew);
            insert.Parameters.AddWithValue((object?)req.ExternalReference ?? DBNull.Value);
            insert.Parameters.AddWithValue(idempotencyKey);
            insert.Parameters.AddWithValue((decimal)quota);
            var subscriptionId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            await InsertSubscriptionEventAsync(connection, transaction, subscriptionId, userId.Value,
                "purchased", idempotencyKey, ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/user/subscriptions/{subscriptionId}",
                new { id = subscriptionId, status = "active", duplicate = false });
        });

        user.MapPost("/{id:long}/renew", async (long id, ClaimsPrincipal principal,
            SubscriptionRenewRequest req, NpgsqlDataSource dataSource, HttpRequest http,
            CancellationToken ct) =>
        {
            var idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
                return Results.BadRequest(new { error = "Idempotency-Key is required" });
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var userId = await FindUserIdAsync(connection, principal.Identity?.Name, ct);
            if (userId is null) return Results.Unauthorized();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var eventExists = await ScalarLongAsync(connection, transaction,
                "SELECT id FROM subscription_events WHERE user_id = $1 AND event_type = 'renewed' AND idempotency_key = $2 FOR UPDATE",
                ct, userId.Value, idempotencyKey);
            if (eventExists is not null)
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new { id, duplicate = true, status = "active" });
            }
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE user_subscriptions
                SET status = 'active', cancelled_at = NULL,
                    expires_at = GREATEST(COALESCE(expires_at, now()), now()) + interval '30 days',
                    renewal_at = CASE WHEN $1 THEN GREATEST(COALESCE(expires_at, now()), now()) + interval '30 days' ELSE NULL END
                WHERE id = $2 AND user_id = $3 AND status IN ('active', 'cancelled', 'expired', 'past_due')
                RETURNING id
                """;
            update.Parameters.AddWithValue(req.AutoRenew);
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(userId.Value);
            var renewedId = await update.ExecuteScalarAsync(ct);
            if (renewedId is null)
                return Results.NotFound(new { error = "Subscription not found" });
            await InsertSubscriptionEventAsync(connection, transaction, id, userId.Value,
                "renewed", idempotencyKey, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { id, status = "active", duplicate = false });
        });

        user.MapPost("/{id:long}/cancel", async (long id, ClaimsPrincipal principal,
            NpgsqlDataSource dataSource, HttpRequest http, CancellationToken ct) =>
        {
            var idempotencyKey = http.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
                return Results.BadRequest(new { error = "Idempotency-Key is required" });
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var userId = await FindUserIdAsync(connection, principal.Identity?.Name, ct);
            if (userId is null) return Results.Unauthorized();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var eventExists = await ScalarLongAsync(connection, transaction,
                "SELECT id FROM subscription_events WHERE user_id = $1 AND event_type = 'cancelled' AND idempotency_key = $2 FOR UPDATE",
                ct, userId.Value, idempotencyKey);
            if (eventExists is not null)
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new { id, duplicate = true, status = "cancelled" });
            }
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE user_subscriptions SET status = 'cancelled', cancelled_at = now(), renewal_at = NULL
                WHERE id = $1 AND user_id = $2 AND status = 'active' RETURNING id
                """;
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(userId.Value);
            var cancelledId = await update.ExecuteScalarAsync(ct);
            if (cancelledId is null)
                return Results.NotFound(new { error = "Active subscription not found" });
            await InsertSubscriptionEventAsync(connection, transaction, id, userId.Value,
                "cancelled", idempotencyKey, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { id, status = "cancelled", duplicate = false });
        });
    }

    private static void MapPricing(WebApplication app)
    {
        var admin = app.MapGroup("/admin/pricing").RequireAuthorization("AdminOnly");

        admin.MapGet("/versions", async (NpgsqlDataSource dataSource, string? model,
            CancellationToken ct) =>
        {
            await using var command = dataSource.CreateCommand("""
                SELECT version, model, input_usd_per_million, output_usd_per_million,
                       cache_read_usd_per_million, cache_write_usd_per_million,
                       effective_from, effective_until, created_at
                FROM pricing_versions
                WHERE ($1::text IS NULL OR model = $1)
                ORDER BY effective_from DESC, version DESC
                """);
            command.Parameters.AddWithValue((object?)model ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<object>();
            while (await reader.ReadAsync(ct))
            {
                items.Add(new
                {
                    version = reader.GetString(0), model = reader.GetString(1),
                    inputUsdPerMillion = reader.GetDecimal(2), outputUsdPerMillion = reader.GetDecimal(3),
                    cacheReadUsdPerMillion = reader.GetDecimal(4), cacheWriteUsdPerMillion = reader.GetDecimal(5),
                    effectiveFrom = reader.GetFieldValue<DateTime>(6),
                    effectiveUntil = reader.IsDBNull(7) ? (DateTime?)null : reader.GetFieldValue<DateTime>(7),
                    createdAt = reader.GetFieldValue<DateTime>(8),
                });
            }
            return Results.Ok(new { items });
        });

        admin.MapPost("/versions", async (PricingVersionRequest req,
            NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Version) || req.Version.Length > 120
                || string.IsNullOrWhiteSpace(req.Model) || req.Model.Length > 200
                || req.InputUsdPerMillion < 0 || req.OutputUsdPerMillion < 0
                || req.CacheReadUsdPerMillion < 0 || req.CacheWriteUsdPerMillion < 0)
                return Results.BadRequest(new { error = "Invalid pricing version" });
            var effectiveFrom = req.EffectiveFrom.Kind == DateTimeKind.Utc
                ? req.EffectiveFrom : req.EffectiveFrom.ToUniversalTime();
            DateTime? effectiveUntil = req.EffectiveUntil is null ? null
                : (req.EffectiveUntil.Value.Kind == DateTimeKind.Utc
                    ? req.EffectiveUntil.Value : req.EffectiveUntil.Value.ToUniversalTime());
            if (effectiveUntil.HasValue && effectiveUntil <= effectiveFrom)
                return Results.BadRequest(new { error = "EffectiveUntil must be after EffectiveFrom" });

            await using var command = dataSource.CreateCommand("""
                INSERT INTO pricing_versions
                    (version, model, input_usd_per_million, output_usd_per_million,
                     cache_read_usd_per_million, cache_write_usd_per_million,
                     effective_from, effective_until)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                ON CONFLICT (version) DO NOTHING
                """);
            command.Parameters.AddWithValue(req.Version.Trim());
            command.Parameters.AddWithValue(req.Model.Trim());
            command.Parameters.AddWithValue(req.InputUsdPerMillion);
            command.Parameters.AddWithValue(req.OutputUsdPerMillion);
            command.Parameters.AddWithValue(req.CacheReadUsdPerMillion);
            command.Parameters.AddWithValue(req.CacheWriteUsdPerMillion);
            command.Parameters.AddWithValue(effectiveFrom);
            command.Parameters.AddWithValue((object?)effectiveUntil ?? DBNull.Value);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                return Results.Conflict(new { error = "Pricing version already exists" });
            return Results.Created($"/admin/pricing/versions/{req.Version}",
                new { version = req.Version.Trim(), model = req.Model.Trim(), effectiveFrom });
        });

        admin.MapPost("/versions/{version}/close", async (string version,
            NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            await using var command = dataSource.CreateCommand("""
                UPDATE pricing_versions SET effective_until = now()
                WHERE version = $1 AND effective_until IS NULL
                RETURNING effective_until
                """);
            command.Parameters.AddWithValue(version);
            var closedAt = await command.ExecuteScalarAsync(ct);
            return closedAt is null
                ? Results.NotFound(new { error = "Open pricing version not found" })
                : Results.Ok(new { version, effectiveUntil = (DateTime)closedAt });
        });
    }

    private static async Task<long?> FindUserIdAsync(NpgsqlConnection connection, string? email,
        CancellationToken ct, NpgsqlTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM user_accounts WHERE email = $1 AND status = 'active'";
        command.Parameters.AddWithValue(email.Trim().ToLowerInvariant());
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task ExpireSubscriptionsAsync(NpgsqlConnection connection, long userId,
        CancellationToken ct, NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE user_subscriptions SET status = 'expired' WHERE user_id = $1 AND status = 'active' AND expires_at IS NOT NULL AND expires_at <= now()";
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long?> ScalarLongAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task InsertSubscriptionEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, long subscriptionId, long userId, string eventType,
        string idempotencyKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO subscription_events(subscription_id, user_id, event_type, idempotency_key)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (user_id, event_type, idempotency_key) DO NOTHING
            """;
        command.Parameters.AddWithValue(subscriptionId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(idempotencyKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<object>> ReadSubscriptionsAsync(NpgsqlConnection connection,
        long userId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.plan_id, p.name, p.price_monthly, p.quota_usd, s.status,
                   s.started_at, s.expires_at, s.renewal_at, s.cancelled_at,
                   s.quota_granted_usd, s.quota_used_usd
            FROM user_subscriptions s JOIN subscription_plans p ON p.id = s.plan_id
            WHERE s.user_id = $1 ORDER BY s.started_at DESC
            """;
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                id = reader.GetInt64(0), planId = reader.GetInt64(1), name = reader.GetString(2),
                priceMonthly = reader.GetDecimal(3), quotaUsd = reader.GetDecimal(4),
                status = reader.GetString(5), startedAt = reader.GetFieldValue<DateTime>(6),
                expiresAt = reader.IsDBNull(7) ? (DateTime?)null : reader.GetFieldValue<DateTime>(7),
                renewalAt = reader.IsDBNull(8) ? (DateTime?)null : reader.GetFieldValue<DateTime>(8),
                cancelledAt = reader.IsDBNull(9) ? (DateTime?)null : reader.GetFieldValue<DateTime>(9),
                quotaGrantedUsd = reader.GetDecimal(10), quotaUsedUsd = reader.GetDecimal(11),
            });
        }
        return items;
    }

    private static void MapRedeemCodes(WebApplication app)
    {
        var admin = app.MapGroup("/admin/redeem-codes").RequireAuthorization("AdminOnly");
        var userGroup = app.MapGroup("/user/redeem").RequireAuthorization("UserOnly");

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

        userGroup.MapPost("/", async (ClaimsPrincipal principal, RedeemRequest req,
            ISqlSugarClient db, NpgsqlDataSource dataSource,
            AccountingStore accounting, AccountingProjectionService projection,
            CancellationToken ct) =>
        {
            var email = principal.Identity?.Name ?? "";
            var user = await db.Queryable<UserAccountEntity>().Where(x => x.Email == email).FirstAsync();
            if (user is null || string.IsNullOrWhiteSpace(req.Code)) return Results.BadRequest(new { error = "Invalid code" });
            var codeText = req.Code.Trim();
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            await using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = """
                SELECT id, bonus_amount, max_uses, used_count, status, expires_at
                FROM redeem_codes WHERE code = $1 FOR UPDATE
                """;
            find.Parameters.AddWithValue(codeText);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.BadRequest(new { error = "Invalid or expired code" });

            var codeId = reader.GetInt64(0);
            var bonus = reader.GetDecimal(1);
            var maxUses = reader.GetInt32(2);
            var usedCount = reader.GetInt32(3);
            var status = reader.GetString(4);
            var expiresAt = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            await reader.DisposeAsync();

            var alreadyRedeemed = false;
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT 1 FROM redeem_code_redemptions WHERE code_id = $1 AND user_id = $2 LIMIT 1";
                existing.Parameters.AddWithValue(codeId);
                existing.Parameters.AddWithValue(user.Id);
                alreadyRedeemed = await existing.ExecuteScalarAsync(ct) is not null;
            }

            if (!alreadyRedeemed && (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                || (expiresAt.HasValue && expiresAt.Value <= DateTime.UtcNow)
                || (maxUses > 0 && usedCount >= maxUses)))
                return Results.BadRequest(new { error = "Code usage limit reached or expired" });

            if (!alreadyRedeemed)
            {
                await using (var redemption = connection.CreateCommand())
                {
                    redemption.Transaction = transaction;
                    redemption.CommandText = """
                        INSERT INTO redeem_code_redemptions(code_id, user_id, bonus_amount)
                        VALUES ($1, $2, $3)
                        ON CONFLICT (code_id, user_id) DO NOTHING
                        RETURNING bonus_amount
                        """;
                    redemption.Parameters.AddWithValue(codeId);
                    redemption.Parameters.AddWithValue(user.Id);
                    redemption.Parameters.AddWithValue(bonus);
                    var inserted = await redemption.ExecuteScalarAsync(ct);
                    if (inserted is null || inserted is DBNull)
                        alreadyRedeemed = true;
                }

                if (!alreadyRedeemed)
                {
                    await using var increment = connection.CreateCommand();
                    increment.Transaction = transaction;
                    increment.CommandText = """
                        UPDATE redeem_codes
                        SET used_count = used_count + 1, last_redeemed_by = $2
                        WHERE id = $1
                        """;
                    increment.Parameters.AddWithValue(codeId);
                    increment.Parameters.AddWithValue(user.Id);
                    await increment.ExecuteNonQueryAsync(ct);
                }
            }

            AccountingEffectResult? effect = null;
            if (bonus != 0)
                effect = await accounting.AppendEffectAsync(connection, transaction,
                    new AccountingEffect(user.Id, $"redeem:{codeId}:{user.Id}",
                        "redeem_bonus", bonus), ct);

            if (effect?.Status == AccountingEffectStatus.Conflict)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { error = "Redeem accounting effect changed" });
            }

            await transaction.CommitAsync(ct);
            if (effect is not null)
                await projection.ApplyAsync(effect.Snapshot, ct);
            return alreadyRedeemed
                ? Results.Conflict(new { error = "Code already redeemed" })
                : Results.Ok(new { message = "Code redeemed", bonus });
        });
    }

    private static void MapReferral(WebApplication app)
    {
        var userGroup = app.MapGroup("/user/referral").RequireAuthorization("UserOnly");
        var admin = app.MapGroup("/admin/referral").RequireAuthorization("AdminOnly");

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
        var group = app.MapGroup("/admin/channel-monitors").RequireAuthorization("AdminOnly");

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
        var group = app.MapGroup("/admin/ops-metrics").RequireAuthorization("AdminOnly");

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
        var group = app.MapGroup("/admin/content-audit").RequireAuthorization("AdminOnly");

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
        var group = app.MapGroup("/admin/proxies").RequireAuthorization("AdminOnly");

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
        var group = app.MapGroup("/admin/tls-fingerprints").RequireAuthorization("AdminOnly");

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
        var group = app.MapGroup("/admin/audit-logs").RequireAuthorization("AdminOnly");

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
        var group = app.MapGroup("/admin/system").RequireAuthorization("AdminOnly");

        group.MapGet("/update-check", async (IHttpClientFactory httpFactory) =>
        {
            try
            {
                var manifestUrl = app.Configuration["Update:ReleaseManifestUrl"];
                if (string.IsNullOrWhiteSpace(manifestUrl))
                    return Results.Ok(new { latest = (string?)null, status = "not_configured" });
                var client = httpFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Platform");
                var resp = await client.GetAsync(manifestUrl);
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
            var manifestUrl = config["Update:ReleaseManifestUrl"];
            if (string.IsNullOrWhiteSpace(manifestUrl))
                return Results.BadRequest(new { error = "Update release manifest is not configured" });
            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Platform");
            var resp = await client.GetAsync(manifestUrl);
            if (!resp.IsSuccessStatusCode)
                return Results.BadRequest(new { error = "Failed to check for updates" });

            var data = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            var tag = data?.GetValueOrDefault("tag_name")?.ToString();
            if (tag is null)
                return Results.BadRequest(new { error = "No release found" });

            var installPath = config["Update:InstallPath"] ?? "/usr/local/bin/platform";
            return Results.Ok(new { message = $"Update to {tag} downloaded. Restart required.", version = tag, path = installPath });
        });

        group.MapPost("/send-email", async (EmailRequest req, IConfiguration config) =>
        {
            var smtpHost = config["Smtp:Host"];
            var smtpPort = int.Parse(config["Smtp:Port"] ?? "587");
            var smtpUser = config["Smtp:Username"] ?? "";
            var smtpPass = config["Smtp:Password"] ?? "";
            var fromAddr = config["Smtp:From"] ?? "noreply@example.invalid";

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

    private record ApiKeySelfServiceRequest(
        string? Name, long? GroupId, decimal? Quota, long? ExpiresAt,
        string[]? IpWhitelist, string[]? IpBlacklist,
        decimal? RateLimit5h, decimal? RateLimit1d, decimal? RateLimit7d,
        string[]? Scopes);

    private static string[] ParseScopes(string? json)
    {
        try { return ApiKeyScopes.Normalize(JsonSerializer.Deserialize<string[]>(json ?? "[\"*\"]")); }
        catch (JsonException) { return [ApiKeyScopes.Wildcard]; }
        catch (ArgumentException) { return [ApiKeyScopes.Wildcard]; }
    }

    private static bool IsFutureExpiry(long? expiresAt) =>
        !expiresAt.HasValue || expiresAt.Value > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private record RedeemRequest(string Code);
    private record ChannelCheckRequest(long AccountId, string Status, int LatencyMs, string? Error);
    private record RestoreRequest(string BackupId);
    private record EmailRequest(string To, string Subject, string Body, Dictionary<string, string>? TemplateVars);
    private record ReferralRecordRequest(long ReferrerUserId, long ReferredUserId, decimal BonusUsd);
    private record ContentCheckRequest(string Content, long UserId, string? RequestId);
}
