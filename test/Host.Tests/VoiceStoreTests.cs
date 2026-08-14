using Npgsql;
using ScalaAPI.Data.Voice;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class VoiceStoreTests
{
    [Fact]
    public async Task CreateAndListByUser()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new VoiceStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);

        var voice = await store.CreateAsync(userId, $"test-voice-{suffix}", "A test voice",
            "custom", "https://example.test/voice.wav", "{}");

        Assert.Equal(userId, voice.UserId);
        Assert.Equal($"test-voice-{suffix}", voice.Name);
        Assert.Equal("custom", voice.VoiceType);
        Assert.Equal("active", voice.Status);

        var voices = await store.ListByUserAsync(userId, null, 10);
        Assert.Contains(voices, v => v.Name == $"test-voice-{suffix}");
    }

    [Fact]
    public async Task GetByUserEnforcesOwnership()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new VoiceStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);
        var otherUserId = await EnsureTestUser(dataSource, "other-" + suffix);

        var voice = await store.CreateAsync(userId, $"owned-voice-{suffix}", "owned",
            "custom", "", "{}");

        var accessible = await store.GetByUserAsync(voice.Id, userId);
        Assert.NotNull(accessible);

        var denied = await store.GetByUserAsync(voice.Id, otherUserId);
        Assert.Null(denied);
    }

    [Fact]
    public async Task UpdateStatusAndDelete()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new VoiceStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);

        var voice = await store.CreateAsync(userId, $"del-voice-{suffix}", "to delete",
            "custom", "", "{}");

        var archived = await store.UpdateStatusAsync(voice.Id, userId, "archived");
        Assert.NotNull(archived);
        Assert.Equal("archived", archived!.Status);

        var deleted = await store.DeleteAsync(voice.Id, userId);
        Assert.True(deleted);

        var gone = await store.GetByIdAsync(voice.Id);
        Assert.Null(gone);
    }

    [Fact]
    public async Task DuplicateNameUpserts()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new VoiceStore(dataSource);

        var suffix = Guid.NewGuid().ToString("N");
        var userId = await EnsureTestUser(dataSource, suffix);

        var first = await store.CreateAsync(userId, $"dup-voice-{suffix}", "first",
            "custom", "", "{}");
        var second = await store.CreateAsync(userId, $"dup-voice-{suffix}", "second",
            "custom", "", "{}");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("second", second.Description);
    }

    private static async Task<long> EnsureTestUser(NpgsqlDataSource dataSource, string suffix)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO user_accounts (email, password_hash, role, status)
            VALUES ($1, 'test', 'user', 'active')
            ON CONFLICT (email) DO UPDATE SET status = 'active'
            RETURNING id
            """);
        cmd.Parameters.AddWithValue($"voice-test-{suffix}@test.local");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
