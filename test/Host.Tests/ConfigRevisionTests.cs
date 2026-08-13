using ScalaAPI.Data.Config;
using ScalaAPI.Grains.Interfaces;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ConfigRevisionTests
{
    [Fact]
    public async Task RevisionIsCreatedOnConfigWrite()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
        var store = new ConfigRevisionStore(dataSource);
        var key = $"test.revision-create-{Guid.NewGuid():N}";

        try
        {
            var revisionId = await store.RecordRevisionAsync(
                key, "value-1", null, 1L, "initial write");

            Assert.True(revisionId > 0);

            var latest = await store.GetLatestRevisionAsync(key);
            Assert.NotNull(latest);
            Assert.Equal(key, latest.ConfigKey);
            Assert.Equal("value-1", latest.ConfigValue);
            Assert.Equal("pending", latest.Status);
            Assert.Null(latest.PreviousRevisionId);
            Assert.Equal(1L, latest.ActorUserId);
            Assert.Equal("initial write", latest.ActorReason);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM config_revisions WHERE config_key = $1");
            cleanup.Parameters.AddWithValue(key);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RollbackMarksRevisionAndCreatesCompensatingEntry()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
        var store = new ConfigRevisionStore(dataSource);
        var key = $"test.revision-rollback-{Guid.NewGuid():N}";

        try
        {
            // Create first revision
            var rev1 = await store.RecordRevisionAsync(key, "value-1", null, 1L, "first");
            await store.MarkAppliedAsync(rev1);

            // Create second revision
            var rev2 = await store.RecordRevisionAsync(key, "value-2", rev1, 1L, "second");
            await store.MarkAppliedAsync(rev2);

            // Rollback the second revision
            var result = await store.RollbackAsync(rev2, 1L, "bad config");
            Assert.True(result);

            // Verify the original revision is marked as rolled back
            var revisions = await store.ListRevisionsAsync(key);
            var rolledBack = revisions.First(r => r.RevisionId == rev2);
            Assert.Equal("rolled_back", rolledBack.Status);
            Assert.NotNull(rolledBack.RolledBackAt);

            // Verify a compensating revision was created (should be the newest)
            var latest = await store.GetLatestRevisionAsync(key);
            Assert.NotNull(latest);
            Assert.NotEqual(rev1, latest.RevisionId);
            Assert.NotEqual(rev2, latest.RevisionId);
            Assert.Equal("value-1", latest.ConfigValue); // restored to first value
            Assert.Equal("pending", latest.Status);
            Assert.Equal(rev2, latest.PreviousRevisionId);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM config_revisions WHERE config_key = $1");
            cleanup.Parameters.AddWithValue(key);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task StaleWriteProtectionSkipsOlderPendingRevision()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
        var store = new ConfigRevisionStore(dataSource);
        var key = $"test.stale-write-{Guid.NewGuid():N}";

        try
        {
            // Create two pending revisions
            var rev1 = await store.RecordRevisionAsync(key, "value-1", null, 1L, "first");
            var rev2 = await store.RecordRevisionAsync(key, "value-2", rev1, 1L, "second");

            // rev1 should have a newer pending revision (rev2)
            var hasNewer = await store.HasNewerPendingRevisionAsync(key, rev1);
            Assert.True(hasNewer);

            // rev2 should NOT have a newer pending revision
            var rev2HasNewer = await store.HasNewerPendingRevisionAsync(key, rev2);
            Assert.False(rev2HasNewer);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM config_revisions WHERE config_key = $1");
            cleanup.Parameters.AddWithValue(key);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task NodeObservationRecordsAndUpdates()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
        var store = new ConfigRevisionStore(dataSource);
        var nodeId = $"test-node-{Guid.NewGuid():N}";

        try
        {
            await store.RecordNodeObservationAsync(nodeId, 10);

            var observations = await store.GetNodeObservationsAsync();
            var obs = observations.FirstOrDefault(o => o.NodeId == nodeId);
            Assert.NotNull(obs);
            Assert.Equal(10, obs.LastSeenRevision);

            // Update the observation
            await store.RecordNodeObservationAsync(nodeId, 20);
            observations = await store.GetNodeObservationsAsync();
            obs = observations.First(o => o.NodeId == nodeId);
            Assert.Equal(20, obs.LastSeenRevision);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM config_node_observations WHERE node_id = $1");
            cleanup.Parameters.AddWithValue(nodeId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void SecretValidationRejectsInlineValues()
    {
        // Inline secret values should be rejected
        Assert.Throws<ArgumentException>(() =>
            ConfigValidation.Validate("security:api-key", "my-secret-value"));

        // References starting with "secret:" should be accepted
        ConfigValidation.Validate("security:api-key", "secret:vault/api-key");
    }

    [Fact]
    public void SecretValidationAcceptsSecretReferences()
    {
        // All sensitive key patterns should accept secret: references
        ConfigValidation.Validate("security:token", "secret:my-vault/token");
        ConfigValidation.Validate("connectionstrings:db", "secret:vault/db-conn");
        ConfigValidation.Validate("app.password", "secret:vault/app-password");
        ConfigValidation.Validate("app.secret", "secret:vault/app-secret");
        ConfigValidation.Validate("app.masterkey", "secret:vault/masterkey");
    }

    [Fact]
    public void IsSensitiveKeyDetectsSensitivePatterns()
    {
        Assert.True(ConfigValidation.IsSensitiveKey("security:token"));
        Assert.True(ConfigValidation.IsSensitiveKey("connectionstrings:db"));
        Assert.True(ConfigValidation.IsSensitiveKey("app.password"));
        Assert.True(ConfigValidation.IsSensitiveKey("app.secret"));
        Assert.True(ConfigValidation.IsSensitiveKey("app.masterkey"));
        Assert.False(ConfigValidation.IsSensitiveKey("feature.dark-mode"));
        Assert.False(ConfigValidation.IsSensitiveKey("app.name"));
    }

    [Fact]
    public async Task MarkAppliedTransitionsPendingToApplied()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
        var store = new ConfigRevisionStore(dataSource);
        var key = $"test.mark-applied-{Guid.NewGuid():N}";

        try
        {
            var revisionId = await store.RecordRevisionAsync(
                key, "value-1", null, 1L, "test");

            var before = await store.GetLatestRevisionAsync(key);
            Assert.Equal("pending", before!.Status);

            await store.MarkAppliedAsync(revisionId);

            var after = await store.GetLatestRevisionAsync(key);
            Assert.Equal("applied", after!.Status);
            Assert.NotNull(after.AppliedAt);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM config_revisions WHERE config_key = $1");
            cleanup.Parameters.AddWithValue(key);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RollbackReturnsFalseForNonexistentRevision()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
        var store = new ConfigRevisionStore(dataSource);

        var result = await store.RollbackAsync(-999, 1L, "nonexistent");
        Assert.False(result);
    }
}
