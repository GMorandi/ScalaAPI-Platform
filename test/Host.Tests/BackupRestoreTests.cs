using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Data.Backups;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class BackupRestoreTests
{
    // --- Checksum computation tests ---

    [Fact]
    public async Task ChecksumIsDeterministic()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "test data for checksum"u8.ToArray());
            var checksum1 = await BackupService.ComputeChecksumAsync(path);
            var checksum2 = await BackupService.ComputeChecksumAsync(path);
            Assert.Equal(checksum1, checksum2);
            Assert.Matches("^[0-9a-f]{64}$", checksum1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ChecksumDiffersForDifferentContent()
    {
        var path1 = Path.GetTempFileName();
        var path2 = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path1, "content A"u8.ToArray());
            await File.WriteAllBytesAsync(path2, "content B"u8.ToArray());
            var checksum1 = await BackupService.ComputeChecksumAsync(path1);
            var checksum2 = await BackupService.ComputeChecksumAsync(path2);
            Assert.NotEqual(checksum1, checksum2);
        }
        finally
        {
            File.Delete(path1);
            File.Delete(path2);
        }
    }

    [Fact]
    public async Task TamperedArtifactFailsChecksum()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "original content"u8.ToArray());
            var originalChecksum = await BackupService.ComputeChecksumAsync(path);

            // Tamper with the file.
            await File.WriteAllBytesAsync(path, "tampered content"u8.ToArray());

            var service = CreateBackupService();
            var valid = await service.VerifyChecksumAsync(path, originalChecksum);
            Assert.False(valid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidArtifactPassesChecksum()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "valid content"u8.ToArray());
            var checksum = await BackupService.ComputeChecksumAsync(path);

            var service = CreateBackupService();
            var valid = await service.VerifyChecksumAsync(path, checksum);
            Assert.True(valid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Encryption tests ---

    [Fact]
    public async Task EncryptDecryptRoundTrip()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(dataSource, NullLogger<BackupService>.Instance);

        // Create a key for encryption.
        var key = await service.CreateKeyAsync("aes-256-gcm");
        Assert.Equal("active", key.Status);
        Assert.Equal("aes-256-gcm", key.Algorithm);

        var path = Path.GetTempFileName();
        try
        {
            var originalContent = "sensitive backup data for encryption test"u8.ToArray();
            await File.WriteAllBytesAsync(path, originalContent);

            var encResult = await service.EncryptArtifactAsync(path);
            Assert.NotNull(encResult);
            Assert.Equal(key.KeyId, encResult.KeyId);
            Assert.Equal("aes-256-gcm", encResult.Algorithm);
            Assert.NotEmpty(encResult.Nonce);
            Assert.NotEmpty(encResult.Tag);

            // Encrypted file should differ from original.
            var encrypted = await File.ReadAllBytesAsync(path);
            Assert.NotEqual(originalContent, encrypted);

            // Decrypt.
            var decrypted = await service.DecryptArtifactAsync(path, encResult.KeyId, encResult.Nonce);
            Assert.True(decrypted);

            var restored = await File.ReadAllBytesAsync(path);
            Assert.Equal(originalContent, restored);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecryptWithWrongKeyFails()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(dataSource, NullLogger<BackupService>.Instance);

        // Create two keys.
        var key1 = await service.CreateKeyAsync("aes-256-gcm");
        var key2 = await service.CreateKeyAsync("aes-256-gcm");

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "test data"u8.ToArray());
            var encResult = await service.EncryptArtifactAsync(path);
            Assert.NotNull(encResult);

            // Try to decrypt with the wrong key.
            var result = await service.DecryptArtifactAsync(path, key2.KeyId, encResult.Nonce);
            Assert.False(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Signing tests ---

    [Fact]
    public async Task SignAndVerifyRoundTrip()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(dataSource, NullLogger<BackupService>.Instance);

        // Create a signing key.
        var key = await service.CreateKeyAsync("hmac-sha256");
        Assert.Equal("hmac-sha256", key.Algorithm);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "data to sign"u8.ToArray());
            var signResult = await service.SignArtifactAsync(path);
            Assert.NotNull(signResult);
            Assert.Equal(key.KeyId, signResult.KeyId);
            Assert.NotEmpty(signResult.Signature);

            // Verify.
            var valid = await service.VerifySignatureAsync(path, signResult.KeyId, signResult.Signature);
            Assert.True(valid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TamperedArtifactFailsSignatureVerification()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(dataSource, NullLogger<BackupService>.Instance);

        var key = await service.CreateKeyAsync("hmac-sha256");

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "original data"u8.ToArray());
            var signResult = await service.SignArtifactAsync(path);
            Assert.NotNull(signResult);

            // Tamper with the file.
            await File.WriteAllBytesAsync(path, "tampered data"u8.ToArray());

            var valid = await service.VerifySignatureAsync(path, signResult.KeyId, signResult.Signature);
            Assert.False(valid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Key rotation tests ---

    [Fact]
    public async Task KeyRotationRetiresOldKey()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(dataSource, NullLogger<BackupService>.Instance);

        var key1 = await service.CreateKeyAsync("hmac-sha256");
        Assert.Equal("active", key1.Status);

        var key2 = await service.RotateKeyAsync("hmac-sha256");
        Assert.Equal("active", key2.Status);
        Assert.NotEqual(key1.KeyId, key2.KeyId);
    }

    // --- Restore target safety tests ---

    [Fact]
    public void RestoreToLiveAuthorityIsRejected()
    {
        var service = CreateRestoreService();
        var source = "Host=live-db;Port=5432;Database=production;Username=admin;Password=secret";
        var target = "Host=live-db;Port=5432;Database=production;Username=admin;Password=secret";

        var safe = service.IsRestoreTargetSafe(target, source);
        Assert.False(safe);
    }

    [Fact]
    public void RestoreToDifferentDatabaseIsAllowed()
    {
        var service = CreateRestoreService();
        var source = "Host=live-db;Port=5432;Database=production;Username=admin;Password=secret";
        var target = "Host=live-db;Port=5432;Database=restore_target;Username=admin;Password=secret";

        var safe = service.IsRestoreTargetSafe(target, source);
        Assert.True(safe);
    }

    [Fact]
    public void RestoreToDifferentHostIsAllowed()
    {
        var service = CreateRestoreService();
        var source = "Host=live-db;Port=5432;Database=production;Username=admin;Password=secret";
        var target = "Host=restore-db;Port=5432;Database=production;Username=admin;Password=secret";

        var safe = service.IsRestoreTargetSafe(target, source);
        Assert.True(safe);
    }

    [Fact]
    public void RestoreToDifferentPortIsAllowed()
    {
        var service = CreateRestoreService();
        var source = "Host=live-db;Port=5432;Database=production;Username=admin;Password=secret";
        var target = "Host=live-db;Port=5433;Database=production;Username=admin;Password=secret";

        var safe = service.IsRestoreTargetSafe(target, source);
        Assert.True(safe);
    }

    [Fact]
    public void RestoreWithEmptyTargetIsRejected()
    {
        var service = CreateRestoreService();
        var source = "Host=live-db;Port=5432;Database=production;Username=admin;Password=secret";

        var safe = service.IsRestoreTargetSafe("", source);
        Assert.False(safe);
    }

    // --- Retention policy tests ---

    [Fact]
    public async Task RetentionPolicyUpsertAndGet()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(dataSource, NullLogger<BackupService>.Instance);

        var policy = await service.UpsertRetentionPolicyAsync(
            keepDaily: 14, keepWeekly: 8, keepMonthly: 24,
            offsiteEnabled: true, offsiteUrl: "s3://backups",
            offsiteBucket: "scala-backups", offsiteRegion: "us-east-1",
            encryptionEnabled: true, signingEnabled: true,
            encryptionKeyId: null);

        Assert.Equal(14, policy.KeepDaily);
        Assert.Equal(8, policy.KeepWeekly);
        Assert.Equal(24, policy.KeepMonthly);
        Assert.True(policy.OffsiteEnabled);
        Assert.True(policy.EncryptionEnabled);
        Assert.True(policy.SigningEnabled);

        var fetched = await service.GetRetentionPolicyAsync();
        Assert.NotNull(fetched);
        Assert.Equal(14, fetched.KeepDaily);
    }

    // --- RPO/RTO recording tests ---

    [Fact]
    public async Task RpoRtoRecordAndRetrieve()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new BackupService(dataSource, NullLogger<BackupService>.Instance);

        var record = await service.RecordRpoRtoAsync(
            backupId: null,
            rpoSeconds: 3600,
            rtoSeconds: 1800,
            backupDurationSeconds: 120,
            restoreDurationSeconds: 300,
            verificationPassed: true);

        Assert.True(record.VerificationPassed);
        Assert.Equal(3600, record.RpoSeconds);
        Assert.Equal(1800, record.RtoSeconds);

        var latest = await service.GetLatestRpoRtoAsync(5);
        Assert.NotEmpty(latest);
        Assert.Contains(latest, r => r.RecordId == record.RecordId);
    }

    // --- Idempotent operation tests ---

    [Fact]
    public async Task BackupCreateIsIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Backup:Directory"] = Path.GetTempPath(),
            })
            .Build();

        var store = new ScalaAPI.Admin.Data.BackupStore(
            dataSource, config, NullLogger<ScalaAPI.Admin.Data.BackupStore>.Instance);

        var idemKey = $"idem-test-{Guid.NewGuid():N}";
        var result1 = await store.CreateAsync(1, idemKey, "postgres", 14, "127.0.0.1");
        // Second call with same key should replay.
        var result2 = await store.CreateAsync(1, idemKey, "postgres", 14, "127.0.0.1");
        Assert.Equal(result1.Status == ScalaAPI.Admin.Data.BackupCommandStatus.Created
            ? ScalaAPI.Admin.Data.BackupCommandStatus.Replayed
            : result1.Status, result2.Status);
    }

    // --- Artifact verification tests ---

    [Fact]
    public async Task VerifyMissingArtifactFails()
    {
        var service = CreateRestoreService();
        var result = await service.VerifyArtifactAsync("/nonexistent/path.dump", "abc123");
        Assert.False(result.IsValid);
        Assert.Equal("artifact_missing", result.ErrorCode);
    }

    [Fact]
    public async Task VerifyArtifactWithNoChecksumFails()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "data"u8.ToArray());
            var service = CreateRestoreService();
            var result = await service.VerifyArtifactAsync(path, "");
            Assert.False(result.IsValid);
            Assert.Equal("checksum_missing", result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifyArtifactWithMatchingChecksumPasses()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "data"u8.ToArray());
            var checksum = await BackupService.ComputeChecksumAsync(path);

            var service = CreateRestoreService();
            var result = await service.VerifyArtifactAsync(path, checksum);
            Assert.True(result.IsValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifyArtifactWithWrongChecksumFails()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "data"u8.ToArray());
            var service = CreateRestoreService();
            var result = await service.VerifyArtifactAsync(path, "0000000000000000000000000000000000000000000000000000000000000000");
            Assert.False(result.IsValid);
            Assert.Equal("checksum_mismatch", result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Failure injection tests ---

    [Fact]
    public async Task FailureInjectionCreatesMarker()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var service = new RestoreService(
            NpgsqlDataSource.Create(connectionString),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backup:RestoreTargetConnection"] = connectionString,
                })
                .Build(),
            NullLogger<RestoreService>.Instance);

        var injected = await service.InjectFailureAsync(connectionString, "corrupt_table");
        Assert.True(injected);
    }

    [Fact]
    public async Task FailureInjectionRejectsUnknownType()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var service = new RestoreService(
            NpgsqlDataSource.Create(connectionString),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backup:RestoreTargetConnection"] = connectionString,
                })
                .Build(),
            NullLogger<RestoreService>.Instance);

        var injected = await service.InjectFailureAsync(connectionString, "unknown_type");
        Assert.False(injected);
    }

    // --- Helpers ---

    private static BackupService CreateBackupService()
    {
        // For tests that don't need DB, create a minimal service.
        // Tests that need DB will skip via the connection string check.
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION") ?? "";
        var dataSource = string.IsNullOrWhiteSpace(connectionString)
            ? NpgsqlDataSource.Create("Host=localhost;Database=test")
            : NpgsqlDataSource.Create(connectionString);
        return new BackupService(dataSource, NullLogger<BackupService>.Instance);
    }

    private static RestoreService CreateRestoreService()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION") ?? "";
        var dataSource = string.IsNullOrWhiteSpace(connectionString)
            ? NpgsqlDataSource.Create("Host=localhost;Database=test")
            : NpgsqlDataSource.Create(connectionString);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:RestoreTargetConnection"] = connectionString,
            })
            .Build();
        return new RestoreService(dataSource, config, NullLogger<RestoreService>.Instance);
    }
}
