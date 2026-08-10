using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class BackupStoreTests
{
    [Fact]
    public async Task GuardRailsRejectInvalidRequestsAndAuthorityRestoreTarget()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Backup:Directory"] = Path.Combine(Path.GetTempPath(), "scalaapi-backup-tests"),
                ["Backup:RestoreTargetConnection"] = connectionString,
                ["Backup:PgDumpPath"] = "/bin/false",
                ["Backup:PgRestorePath"] = "/bin/false",
            })
            .Build();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new BackupStore(dataSource, configuration,
            NullLogger<BackupStore>.Instance);

        var invalidKey = await store.CreateAsync(42, "short", "postgres", 14, null);
        Assert.Equal(BackupCommandStatus.Invalid, invalidKey.Status);
        var invalidKind = await store.CreateAsync(42, "backup-key-invalid-kind", "full", 14, null);
        Assert.Equal(BackupCommandStatus.Invalid, invalidKind.Status);

        var backupId = $"bak_{Guid.NewGuid():N}";
        var artifactDirectory = configuration["Backup:Directory"]!;
        Directory.CreateDirectory(artifactDirectory);
        var artifactName = backupId + ".dump";
        await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, artifactName), [1, 2, 3]);
        try
        {
            await using var insert = dataSource.CreateCommand("""
                INSERT INTO backup_jobs(
                    id, kind, idempotency_key, request_fingerprint, status,
                    artifact_path, size_bytes, sha256, created_by)
                VALUES ($1, 'postgres', $2, $3, 'completed', $4, 3,
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 42)
                """);
            insert.Parameters.AddWithValue(backupId);
            insert.Parameters.AddWithValue($"seed-{Guid.NewGuid():N}");
            insert.Parameters.AddWithValue("seed-fingerprint");
            insert.Parameters.AddWithValue(artifactName);
            await insert.ExecuteNonQueryAsync();

            var authorityRestore = await store.RestoreAsync(42, backupId,
                "restore-authority-key", null);
            Assert.Equal(BackupCommandStatus.Invalid, authorityRestore.Status);
            Assert.Equal("restore_target_is_authority", authorityRestore.ErrorCode);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM backup_jobs WHERE id = $1");
            cleanup.Parameters.AddWithValue(backupId);
            await cleanup.ExecuteNonQueryAsync();
            File.Delete(Path.Combine(artifactDirectory, artifactName));
        }
    }
}
