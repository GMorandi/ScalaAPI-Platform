using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Data.Backups;

namespace ScalaAPI.Admin.Endpoints;

public sealed record BackupCreateRequest(string? Kind, int RetentionDays = 14);

public sealed record RetentionUpdateRequest(
    int KeepDaily = 7,
    int KeepWeekly = 4,
    int KeepMonthly = 12,
    bool OffsiteEnabled = false,
    string? OffsiteUrl = null,
    string? OffsiteBucket = null,
    string? OffsiteRegion = null,
    bool EncryptionEnabled = false,
    bool SigningEnabled = false,
    string? EncryptionKeyId = null);

/// <summary>
/// Bilingual status message for admin operations.
/// </summary>
public sealed record BilingualStatus(string En, string Zh);

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/backups").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (BackupStore backups, int page = 1, int size = 50,
            CancellationToken ct = default) =>
            Results.Ok(new { items = await backups.ListAsync(page, size, ct),
                restore_configured = backups.RestoreConfigured }));

        group.MapGet("/{id}", async (string id, BackupStore backups,
            CancellationToken ct = default) =>
        {
            var job = await backups.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPost("/", async (BackupStore backups, ClaimsPrincipal principal,
            HttpRequest request, BackupCreateRequest body, CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            var result = await backups.CreateAsync(actorId,
                request.Headers["Idempotency-Key"].FirstOrDefault(), body.Kind,
                body.RetentionDays,
                request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            var status = result.Status switch
            {
                BackupCommandStatus.Created => new BilingualStatus("Backup created", "备份已创建"),
                BackupCommandStatus.Replayed => new BilingualStatus("Backup replayed (idempotent)", "备份已重放（幂等）"),
                BackupCommandStatus.Busy => new BilingualStatus("Backup in progress", "备份进行中"),
                BackupCommandStatus.Conflict => new BilingualStatus("Backup conflict", "备份冲突"),
                _ => new BilingualStatus("Invalid request", "无效请求"),
            };
            return result.Status switch
            {
                BackupCommandStatus.Created => Results.Created($"/admin/backups/{result.Job!.Id}",
                    new { job = result.Job, status }),
                BackupCommandStatus.Replayed => Results.Ok(new { job = result.Job, status }),
                BackupCommandStatus.Busy or BackupCommandStatus.Conflict => Results.Conflict(new
                {
                    error = result.ErrorCode,
                    message = result.Error,
                    job = result.Job,
                    status,
                }),
                _ => Results.BadRequest(new { error = result.ErrorCode, message = result.Error, status }),
            };
        });

        group.MapPost("/{id}/restore", async (string id, BackupStore backups,
            ClaimsPrincipal principal, HttpRequest request, CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            var result = await backups.RestoreAsync(actorId, id,
                request.Headers["Idempotency-Key"].FirstOrDefault(),
                request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            var status = result.Status switch
            {
                BackupCommandStatus.Created => new BilingualStatus("Restore started", "恢复已启动"),
                BackupCommandStatus.Replayed => new BilingualStatus("Restore replayed (idempotent)", "恢复已重放（幂等）"),
                BackupCommandStatus.NotFound => new BilingualStatus("Backup not found", "备份未找到"),
                BackupCommandStatus.NotConfigured => new BilingualStatus(
                    "Restore not configured", "恢复未配置"),
                BackupCommandStatus.Invalid when result.ErrorCode == "restore_target_is_authority" =>
                    new BilingualStatus("Restore to live authority is prohibited",
                        "禁止恢复到生产主库"),
                _ => new BilingualStatus("Invalid request", "无效请求"),
            };
            return result.Status switch
            {
                BackupCommandStatus.Created => Results.Accepted($"/admin/backups/{id}/restore",
                    new { run = result.Run, status }),
                BackupCommandStatus.Replayed => Results.Ok(new { run = result.Run, status }),
                BackupCommandStatus.NotFound => Results.NotFound(),
                BackupCommandStatus.NotConfigured => Results.Conflict(
                    new { error = result.ErrorCode, message = result.Error, status }),
                BackupCommandStatus.Busy or BackupCommandStatus.Conflict => Results.Conflict(new
                {
                    error = result.ErrorCode,
                    message = result.Error,
                    run = result.Run,
                    status,
                }),
                _ => Results.BadRequest(new { error = result.ErrorCode, message = result.Error, status }),
            };
        });

        // Retention policy endpoints.
        group.MapGet("/retention", async (BackupService backupService,
            CancellationToken ct = default) =>
        {
            var policy = await backupService.GetRetentionPolicyAsync(ct);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        group.MapPut("/retention", async (BackupService backupService, ClaimsPrincipal principal,
            RetentionUpdateRequest body, CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out _))
                return Results.Unauthorized();
            var policy = await backupService.UpsertRetentionPolicyAsync(
                body.KeepDaily, body.KeepWeekly, body.KeepMonthly,
                body.OffsiteEnabled, body.OffsiteUrl, body.OffsiteBucket, body.OffsiteRegion,
                body.EncryptionEnabled, body.SigningEnabled, body.EncryptionKeyId, ct);
            return Results.Ok(new
            {
                policy,
                status = new BilingualStatus("Retention policy updated", "保留策略已更新"),
            });
        });

        // RPO/RTO measurement endpoints.
        group.MapGet("/rpo-rto", async (BackupService backupService, int limit = 10,
            CancellationToken ct = default) =>
        {
            var records = await backupService.GetLatestRpoRtoAsync(limit, ct);
            return Results.Ok(new { records });
        });

        // Key management endpoints.
        group.MapPost("/keys", async (BackupService backupService, ClaimsPrincipal principal,
            string algorithm, CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out _))
                return Results.Unauthorized();
            if (algorithm is not ("aes-256-gcm" or "hmac-sha256" or "ed25519"))
                return Results.BadRequest(new { error = "unsupported_algorithm",
                    message = "Supported: aes-256-gcm, hmac-sha256, ed25519" });
            var key = await backupService.CreateKeyAsync(algorithm, ct);
            return Results.Created($"/admin/backups/keys/{key.KeyId}", new
            {
                key,
                status = new BilingualStatus("Key created", "密钥已创建"),
            });
        });

        group.MapPost("/keys/rotate", async (BackupService backupService, ClaimsPrincipal principal,
            string algorithm, CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out _))
                return Results.Unauthorized();
            if (algorithm is not ("aes-256-gcm" or "hmac-sha256" or "ed25519"))
                return Results.BadRequest(new { error = "unsupported_algorithm",
                    message = "Supported: aes-256-gcm, hmac-sha256, ed25519" });
            var key = await backupService.RotateKeyAsync(algorithm, ct);
            return Results.Ok(new
            {
                key,
                status = new BilingualStatus("Key rotated", "密钥已轮换"),
            });
        });

        // Runbook endpoint.
        group.MapGet("/runbook", () => Results.Ok(new
        {
            title_en = "Backup & Restore Runbook",
            title_zh = "备份与恢复操作手册",
            steps = new[]
            {
                new
                {
                    step = 1,
                    en = "Verify backup exists and is completed",
                    zh = "确认备份存在且已完成",
                },
                new
                {
                    step = 2,
                    en = "Verify checksum matches recorded value",
                    zh = "验证校验和与记录值匹配",
                },
                new
                {
                    step = 3,
                    en = "Confirm restore target is NOT the live authority database",
                    zh = "确认恢复目标不是生产主库",
                },
                new
                {
                    step = 4,
                    en = "Initiate restore with idempotency key",
                    zh = "使用幂等键启动恢复",
                },
                new
                {
                    step = 5,
                    en = "Post-restore: verify migrations, users, accounting are readable",
                    zh = "恢复后：验证迁移、用户、账务表可读",
                },
                new
                {
                    step = 6,
                    en = "Record RPO/RTO measurement",
                    zh = "记录 RPO/RTO 测量值",
                },
            },
            rpo_target_seconds = 3600,
            rto_target_seconds = 1800,
        }));
    }
}
