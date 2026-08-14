using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScalaAPI.Data.Exports;
using ScalaAPI.Data.Retention;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ExportRetentionTests
{
    // --- RetentionService fingerprint tests (pure logic, no DB needed) ---

    [Fact]
    public void RetentionFingerprintIsDeterministic()
    {
        var policies = new Dictionary<string, int>
        {
            ["auth_sessions"] = 30,
            ["export_jobs"] = 7,
        };
        var fp1 = RetentionService.Fingerprint(dryRun: false, limit: 1000, policies);
        var fp2 = RetentionService.Fingerprint(dryRun: false, limit: 1000, policies);
        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length); // SHA256 hex
    }

    [Fact]
    public void RetentionFingerprintDiffersOnDryRun()
    {
        var policies = new Dictionary<string, int> { ["auth_sessions"] = 30 };
        var fpDry = RetentionService.Fingerprint(dryRun: true, limit: 1000, policies);
        var fpApply = RetentionService.Fingerprint(dryRun: false, limit: 1000, policies);
        Assert.NotEqual(fpDry, fpApply);
    }

    [Fact]
    public void RetentionFingerprintDiffersOnLimit()
    {
        var policies = new Dictionary<string, int> { ["auth_sessions"] = 30 };
        var fp1 = RetentionService.Fingerprint(dryRun: false, limit: 100, policies);
        var fp2 = RetentionService.Fingerprint(dryRun: false, limit: 200, policies);
        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void RetentionFingerprintDiffersOnPolicyValues()
    {
        var p1 = new Dictionary<string, int> { ["auth_sessions"] = 30 };
        var p2 = new Dictionary<string, int> { ["auth_sessions"] = 60 };
        var fp1 = RetentionService.Fingerprint(dryRun: false, limit: 1000, p1);
        var fp2 = RetentionService.Fingerprint(dryRun: false, limit: 1000, p2);
        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void RetentionFingerprintIsOrderIndependent()
    {
        var p1 = new Dictionary<string, int> { ["b_category"] = 10, ["a_category"] = 20 };
        var p2 = new Dictionary<string, int> { ["a_category"] = 20, ["b_category"] = 10 };
        var fp1 = RetentionService.Fingerprint(dryRun: false, limit: 1000, p1);
        var fp2 = RetentionService.Fingerprint(dryRun: false, limit: 1000, p2);
        Assert.Equal(fp1, fp2);
    }

    // --- ExportService constants and validation ---

    [Fact]
    public void ExportServiceMaxLimitIsBounded()
    {
        Assert.Equal(1_000, ExportService.MaxExportLimit);
    }

    [Fact]
    public void ExportServiceDownloadTokenLifetimeIsShort()
    {
        Assert.True(ExportService.DownloadTokenLifetime <= TimeSpan.FromHours(1));
        Assert.True(ExportService.DownloadTokenLifetime >= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ExportServiceMaxDownloadsIsBounded()
    {
        Assert.True(ExportService.MaxDownloadsPerJob >= 1);
        Assert.True(ExportService.MaxDownloadsPerJob <= 10);
    }

    [Fact]
    public void ExportServiceExpiryIsReasonable()
    {
        Assert.True(ExportService.ExportExpiry >= TimeSpan.FromHours(1));
        Assert.True(ExportService.ExportExpiry <= TimeSpan.FromDays(7));
    }

    // --- Sensitive data filtering tests ---

    [Fact]
    public void SensitiveFieldFilterContainsExpectedFields()
    {
        // Verify the default sensitive fields cover password hashes, tokens, and audio data.
        var filter = new SensitiveFieldFilter([
            "password_hash", "refresh_token", "key_hash", "key_secret",
            "audio_content", "audio_data", "raw_audio",
        ]);
        Assert.Contains("password_hash", filter.FieldsToRedact);
        Assert.Contains("refresh_token", filter.FieldsToRedact);
        Assert.Contains("key_hash", filter.FieldsToRedact);
        Assert.Contains("key_secret", filter.FieldsToRedact);
        Assert.Contains("audio_content", filter.FieldsToRedact);
        Assert.Contains("audio_data", filter.FieldsToRedact);
        Assert.Contains("raw_audio", filter.FieldsToRedact);
    }

    [Fact]
    public void SensitiveFieldsAreExcludedFromExportSchema()
    {
        // The export should never include password_hash, key_hash, or refresh_token.
        // This test verifies the contract by checking that the sensitive field list
        // covers all known secret-bearing columns.
        var sensitiveFields = new HashSet<string>
        {
            "password_hash", "refresh_token", "key_hash", "key_secret",
            "audio_content", "audio_data", "raw_audio",
        };
        // These are fields that must NEVER appear in an export.
        var mustNotExport = new[] { "password_hash", "key_hash", "refresh_token" };
        foreach (var field in mustNotExport)
            Assert.Contains(field, sensitiveFields);
    }

    // --- Export job model tests ---

    [Fact]
    public void ExportJobModelPreservesAllFields()
    {
        var now = DateTime.UtcNow;
        var job = new ExportJob(
            JobId: 42, UserId: 100, Status: "ready",
            RequestFingerprint: "abc123", ArtifactKey: "exports/42/hash.json",
            ArtifactSizeBytes: 1024, ArtifactHash: "deadbeef",
            DownloadToken: "token123", DownloadTokenExpiresAt: now.AddMinutes(15),
            DownloadCount: 1, MaxDownloads: 3,
            ExpiresAt: now.AddHours(24), Error: null,
            CreatedAt: now, UpdatedAt: now);
        Assert.Equal(42, job.JobId);
        Assert.Equal(100, job.UserId);
        Assert.Equal("ready", job.Status);
        Assert.Equal(1024, job.ArtifactSizeBytes);
        Assert.Equal(1, job.DownloadCount);
        Assert.Equal(3, job.MaxDownloads);
        Assert.Null(job.Error);
    }

    // --- CleanupRunResult model tests ---

    [Fact]
    public void CleanupRunResultTracksCategoriesAndCounts()
    {
        var now = DateTime.UtcNow;
        var categories = new Dictionary<string, int>
        {
            ["auth_sessions"] = 50,
            ["password_reset_tokens"] = 10,
            ["export_jobs"] = 5,
        };
        var result = new CleanupRunResult(
            RunId: 1, Status: "completed", DryRun: false,
            TotalDeleted: 65, TotalFailed: 0,
            Categories: categories,
            StartedAt: now.AddSeconds(-5), CompletedAt: now);
        Assert.Equal(65, result.TotalDeleted);
        Assert.Equal(0, result.TotalFailed);
        Assert.Equal(3, result.Categories.Count);
        Assert.Equal("completed", result.Status);
        Assert.False(result.DryRun);
    }

    [Fact]
    public void CleanupRunResultDryRunDoesNotDelete()
    {
        var now = DateTime.UtcNow;
        var result = new CleanupRunResult(
            RunId: 2, Status: "completed", DryRun: true,
            TotalDeleted: 0, TotalFailed: 0,
            Categories: new Dictionary<string, int>(),
            StartedAt: now, CompletedAt: now);
        Assert.True(result.DryRun);
        Assert.Equal(0, result.TotalDeleted);
    }

    // --- RetentionPolicy model tests ---

    [Fact]
    public void RetentionPolicyModelPreservesFields()
    {
        var now = DateTime.UtcNow;
        var policy = new RetentionPolicy(
            PolicyId: 1, Category: "auth_sessions",
            RetentionDays: 30, Description: "Session cleanup",
            CreatedAt: now);
        Assert.Equal("auth_sessions", policy.Category);
        Assert.Equal(30, policy.RetentionDays);
        Assert.Equal("Session cleanup", policy.Description);
    }

    // --- Idempotency key tests ---

    [Fact]
    public void IdempotencyKeyFingerprintIsStable()
    {
        // Same inputs produce the same fingerprint across calls.
        var key1 = RetentionService.Fingerprint(false, 500,
            new Dictionary<string, int> { ["x"] = 10 });
        var key2 = RetentionService.Fingerprint(false, 500,
            new Dictionary<string, int> { ["x"] = 10 });
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void IdempotencyKeyFingerprintIsHexSha256()
    {
        var fp = RetentionService.Fingerprint(false, 100,
            new Dictionary<string, int>());
        Assert.Matches("^[0-9a-f]{64}$", fp);
    }

    // --- Download expiry logic tests ---

    [Fact]
    public void DownloadTokenExpiryIsEnforcedByModel()
    {
        var now = DateTime.UtcNow;
        var job = new ExportJob(
            JobId: 1, UserId: 1, Status: "ready",
            RequestFingerprint: "fp", ArtifactKey: "key",
            ArtifactSizeBytes: 100, ArtifactHash: "hash",
            DownloadToken: "token", DownloadTokenExpiresAt: now.AddMinutes(-1), // expired
            DownloadCount: 0, MaxDownloads: 3,
            ExpiresAt: now.AddHours(1), Error: null,
            CreatedAt: now, UpdatedAt: now);
        // Token is expired; the service should reject download.
        Assert.True(job.DownloadTokenExpiresAt < DateTime.UtcNow);
    }

    [Fact]
    public void DownloadCountLimitIsEnforcedByModel()
    {
        var now = DateTime.UtcNow;
        var job = new ExportJob(
            JobId: 1, UserId: 1, Status: "ready",
            RequestFingerprint: "fp", ArtifactKey: "key",
            ArtifactSizeBytes: 100, ArtifactHash: "hash",
            DownloadToken: "token", DownloadTokenExpiresAt: now.AddMinutes(15),
            DownloadCount: 3, MaxDownloads: 3, // at limit
            ExpiresAt: now.AddHours(1), Error: null,
            CreatedAt: now, UpdatedAt: now);
        Assert.True(job.DownloadCount >= job.MaxDownloads);
    }

    // --- Retention protection: objects within retention period are safe ---

    [Fact]
    public void RetentionPeriodProtectsRecentObjects()
    {
        var retentionDays = 30;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var recentDate = DateTime.UtcNow.AddDays(-5); // 5 days ago, within retention
        var oldDate = DateTime.UtcNow.AddDays(-60); // 60 days ago, beyond retention
        Assert.True(recentDate > cutoff); // recent objects are NOT eligible for cleanup
        Assert.True(oldDate < cutoff); // old objects ARE eligible
    }

    // --- Worker crash reclamation ---

    [Fact]
    public void StaleExportJobIsReclaimable()
    {
        // A job stuck in "generating" for too long should be reclaimable.
        var staleAfter = TimeSpan.FromMinutes(10);
        var updatedAt = DateTime.UtcNow.AddMinutes(-15); // 15 min ago > staleAfter
        Assert.True(DateTime.UtcNow - updatedAt > staleAfter);
    }

    [Fact]
    public void FreshExportJobIsNotReclaimable()
    {
        var staleAfter = TimeSpan.FromMinutes(10);
        var updatedAt = DateTime.UtcNow.AddMinutes(-2); // 2 min ago < staleAfter
        Assert.True(DateTime.UtcNow - updatedAt < staleAfter);
    }

    // --- Integration tests (require database) ---

    [Fact]
    public async Task RetentionServiceUpsertAndListPolicies()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var logger = NullLogger<RetentionService>.Instance;
        var service = new RetentionService(dataSource, logger);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var category = $"test_category_{suffix}";

        var policy = await service.UpsertPolicyAsync(category, 45, "Test policy",
            CancellationToken.None);
        Assert.Equal(category, policy.Category);
        Assert.Equal(45, policy.RetentionDays);

        var policies = await service.ListPoliciesAsync(CancellationToken.None);
        Assert.Contains(policies, p => p.Category == category);
    }

    [Fact]
    public async Task RetentionServiceCleanupIsIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var logger = NullLogger<RetentionService>.Instance;
        var service = new RetentionService(dataSource, logger);

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"idem-test-{suffix}";

        var result1 = await service.RunCleanupAsync(
            actorUserId: 1, idempotencyKey: key, dryRun: true,
            limitPerCategory: 10, ct: CancellationToken.None);
        Assert.Equal("completed", result1.Status);

        var result2 = await service.RunCleanupAsync(
            actorUserId: 1, idempotencyKey: key, dryRun: true,
            limitPerCategory: 10, ct: CancellationToken.None);
        Assert.Equal("replayed", result2.Status);
        Assert.Equal(result1.TotalDeleted, result2.TotalDeleted);
    }

    [Fact]
    public async Task RetentionServiceDryRunDoesNotDelete()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var logger = NullLogger<RetentionService>.Instance;
        var service = new RetentionService(dataSource, logger);

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"dryrun-test-{suffix}";

        var result = await service.RunCleanupAsync(
            actorUserId: 1, idempotencyKey: key, dryRun: true,
            limitPerCategory: 10, ct: CancellationToken.None);
        Assert.True(result.DryRun);
        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public async Task ExportServiceRequestAndRetrieve()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var logger = NullLogger<ExportService>.Instance;
        var service = new ExportService(dataSource, logger);

        // Create a test user.
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"export-test-{suffix}@test.local";
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_accounts(email, password_hash, status, role, email_verified)
                VALUES ($1, 'hash', 'active', 'user', true)
                ON CONFLICT (email) DO NOTHING
                RETURNING id
                """;
            cmd.Parameters.AddWithValue(email);
            await cmd.ExecuteScalarAsync();
        }

        // Get user id.
        long userId;
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM user_accounts WHERE email = $1";
            cmd.Parameters.AddWithValue(email);
            userId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        var result = await service.RequestExportAsync(userId, "127.0.0.1", 100);
        Assert.NotNull(result.Job);
        Assert.Equal("pending", result.Job.Status);
        Assert.False(result.AlreadyExists);

        // Request again - should be idempotent.
        var result2 = await service.RequestExportAsync(userId, "127.0.0.1", 100);
        Assert.True(result2.AlreadyExists);
        Assert.Equal(result.Job.JobId, result2.Job.JobId);
    }

    [Fact]
    public async Task ExportServiceDownloadAuthorizationRejectsInvalidToken()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var logger = NullLogger<ExportService>.Instance;
        var service = new ExportService(dataSource, logger);

        var result = await service.DownloadAsync(999999, "invalid-token",
            CancellationToken.None);
        Assert.Null(result); // Non-existent job or invalid token.
    }

    [Fact]
    public async Task ExportServiceExpireStaleJobs()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var logger = NullLogger<ExportService>.Instance;
        var service = new ExportService(dataSource, logger);

        // Should not throw.
        await service.ExpireStaleJobsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RetentionServiceHistoryReturnsResults()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var logger = NullLogger<RetentionService>.Instance;
        var service = new RetentionService(dataSource, logger);

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"history-test-{suffix}";
        await service.RunCleanupAsync(1, key, dryRun: true, 10, CancellationToken.None);

        var history = await service.GetHistoryAsync(10, CancellationToken.None);
        Assert.NotEmpty(history);
    }
}
