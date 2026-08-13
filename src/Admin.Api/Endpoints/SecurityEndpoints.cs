using System.Security.Claims;
using System.Text.Json;
using Npgsql;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data.Audit;
using ScalaAPI.Admin.Security;

namespace ScalaAPI.Admin.Endpoints;

public static class SecurityEndpoints
{
    public static void MapSecurityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/security").RequireAuthorization("AdminOnly");

        // Rotate master key (step-up auth required, enforced by SecurityMiddleware)
        group.MapPost("/rotate-master-key", async (
            RotateKeyRequest req,
            SecretProtector protector,
            AuditLogService auditLog,
            ClaimsPrincipal principal,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();

            byte[] newKey;
            try
            {
                newKey = Convert.FromBase64String(req.NewMasterKey);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid_key_format" });
            }

            if (newKey.Length != 32)
                return Results.BadRequest(new { error = "key_must_be_32_bytes" });

            var oldKeyId = protector.CurrentKeyId;
            var newKeyId = protector.RotateMasterKey(newKey);

            // Record rotation in audit log
            await auditLog.AppendAsync(
                eventType: "security",
                action: "master_key_rotation",
                result: "success",
                actorUserId: actorId,
                actorIp: context.Connection.RemoteIpAddress?.ToString(),
                details: JsonSerializer.Serialize(new { oldKeyId, newKeyId }));

            return Results.Ok(new { oldKeyId, newKeyId, status = "rotated" });
        });

        // Export immutable audit log (admin only)
        group.MapGet("/audit-log", async (
            AuditLogService auditLog,
            string? eventType, string? action,
            DateTime? from, DateTime? to,
            int page = 1, int size = 50,
            CancellationToken ct = default) =>
        {
            var result = await auditLog.ExportAsync(eventType, action, from, to, page, size, ct);
            return Results.Ok(new
            {
                items = result.Items,
                total = result.Total,
                page = result.Page,
                size = result.Size
            });
        });

        // List certificates with status
        group.MapGet("/certificates", (CertificateTracker tracker) =>
        {
            var certs = tracker.ListCertificates();
            return Results.Ok(new { certificates = certs });
        });

        // Trigger security scan (redaction verification, secret leak check)
        group.MapPost("/scan", async (
            SecretRedactionService redaction,
            CertificateTracker certTracker,
            NpgsqlDataSource dataSource,
            CancellationToken ct) =>
        {
            var findings = new List<string>();

            // Check certificate health
            var alerts = certTracker.RefreshAndAlert();
            foreach (var alert in alerts)
            {
                findings.Add($"cert:{alert.CertId}:status={alert.Status}:expires={alert.NotAfter:O}");
            }

            // Verify redaction patterns work
            var testPayload = "api_key=sk-test-12345 password=hunter2 Bearer eyJfake";
            var redacted = redaction.Redact(testPayload);
            if (redacted.Contains("sk-test-12345") || redacted.Contains("hunter2") || redacted.Contains("eyJfake"))
            {
                findings.Add("redaction:FAIL:secrets_leaked_in_test_payload");
            }
            else
            {
                findings.Add("redaction:PASS:all_patterns_redacted");
            }

            // Check for secrets in recent audit log details
            await using var cmd = dataSource.CreateCommand("""
                SELECT count(*) FROM audit_logs
                WHERE created_at > now() - interval '1 hour'
                  AND (details ILIKE '%password=%' OR details ILIKE '%api_key=%')
                """);
            var leakCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            if (leakCount > 0)
            {
                findings.Add($"audit_leak_risk:{leakCount}_recent_entries_contain_plaintext_secrets");
            }

            return Results.Ok(new
            {
                scanned_at = DateTime.UtcNow,
                findings,
                status = findings.Any(f => f.Contains("FAIL")) ? "issues_found" : "clean"
            });
        });
    }

    public record RotateKeyRequest(string NewMasterKey);
}
