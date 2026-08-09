using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ContentPolicyPropagationTests
{
    [Fact]
    public async Task ChangeOutboxPropagatesRevisionAndInvalidationToGarnet()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var eventId = await InsertEventAsync(dataSource, revision);
        try
        {
            var garnet = new RecordingGarnet();
            var service = new ContentPolicyPropagationService(
                dataSource, garnet, NullLogger<ContentPolicyPropagationService>.Instance);

            var result = await service.PropagateOnceAsync($"test-{Guid.NewGuid():N}");

            Assert.Equal(1, result.Claimed);
            Assert.Equal(1, result.Propagated);
            Assert.Equal(0, result.Failed);
            Assert.Contains(garnet.SetCalls, call =>
                call.Key == GarnetKeyspace.ContentPolicyRevision
                && call.Value == revision.ToString());
            Assert.Contains(GarnetKeyspace.InvalidationVersion, garnet.Increments);
            Assert.True(await IsPropagatedAsync(dataSource, eventId));
        }
        finally
        {
            await DeleteEventAsync(dataSource, eventId);
        }
    }

    [Fact]
    public async Task FailedPropagationLeavesRetryableErrorAndCanRecover()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1;
        var eventId = await InsertEventAsync(dataSource, revision);
        try
        {
            var garnet = new RecordingGarnet { Fail = true };
            var service = new ContentPolicyPropagationService(
                dataSource, garnet, NullLogger<ContentPolicyPropagationService>.Instance);

            var failed = await service.PropagateOnceAsync($"test-{Guid.NewGuid():N}");
            Assert.Equal(1, failed.Failed);
            Assert.False(await IsPropagatedAsync(dataSource, eventId));
            Assert.NotNull(await LastErrorAsync(dataSource, eventId));

            garnet.Fail = false;
            var recovered = await service.PropagateOnceAsync($"test-{Guid.NewGuid():N}");
            Assert.Equal(1, recovered.Propagated);
            Assert.True(await IsPropagatedAsync(dataSource, eventId));
        }
        finally
        {
            await DeleteEventAsync(dataSource, eventId);
        }
    }

    private static async Task<long> InsertEventAsync(NpgsqlDataSource dataSource, long revision)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO content_policy_change_events(revision, action, details)
            VALUES ($1, 'updated', '{}'::jsonb)
            RETURNING id
            """);
        command.Parameters.AddWithValue(revision);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> IsPropagatedAsync(NpgsqlDataSource dataSource, long eventId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT propagated_at IS NOT NULL FROM content_policy_change_events WHERE id = $1");
        command.Parameters.AddWithValue(eventId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> LastErrorAsync(NpgsqlDataSource dataSource, long eventId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT last_error FROM content_policy_change_events WHERE id = $1");
        command.Parameters.AddWithValue(eventId);
        return (string?)(await command.ExecuteScalarAsync());
    }

    private static async Task DeleteEventAsync(NpgsqlDataSource dataSource, long eventId)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM content_policy_change_events WHERE id = $1");
        command.Parameters.AddWithValue(eventId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingGarnet : IGarnetService
    {
        public bool Fail { get; set; }
        public List<(string Key, string Value)> SetCalls { get; } = [];
        public List<string> Increments { get; } = [];

        public void Set(string key, string value, TimeSpan? ttl = null)
        {
            if (Fail) throw new InvalidOperationException("garnet unavailable");
            SetCalls.Add((key, value));
        }

        public string? Get(string key) => null;
        public void Delete(string key) { }

        public long Increment(string key)
        {
            if (Fail) throw new InvalidOperationException("garnet unavailable");
            Increments.Add(key);
            return 1;
        }

        public bool Ping() => !Fail;
    }
}
