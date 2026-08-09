using System.Security.Cryptography;
using System.Text;
using Npgsql;
using ScalaAPI.Admin.Auth;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class OAuthStateServiceTests
{
    [Fact]
    public async Task StateBindsProviderRedirectAndVerifierAndIsConsumedOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var now = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var service = new OAuthStateService(dataSource, now);
        var issued = await service.IssueAsync("GitHub", "https://app.scalaapi.test/oauth/callback");
        Assert.NotNull(issued);
        Assert.Equal("github", issued.Provider);
        Assert.InRange(issued.CodeVerifier.Length, 43, 128);
        Assert.NotEqual(issued.CodeVerifier, issued.State);

        try
        {
            var wrongVerifier = await service.ConsumeAsync(issued.Provider, issued.State,
                issued.RedirectUri, new string('a', 64));
            Assert.Equal(OAuthStateStatus.Invalid, wrongVerifier.Status);

            var wrongProvider = await service.ConsumeAsync("google", issued.State,
                issued.RedirectUri, issued.CodeVerifier);
            Assert.Equal(OAuthStateStatus.Invalid, wrongProvider.Status);

            var accepted = await service.ConsumeAsync(issued.Provider, issued.State,
                issued.RedirectUri, issued.CodeVerifier);
            Assert.True(accepted.Accepted);

            var replay = await service.ConsumeAsync(issued.Provider, issued.State,
                issued.RedirectUri, issued.CodeVerifier);
            Assert.Equal(OAuthStateStatus.Replayed, replay.Status);

            await using var row = dataSource.CreateCommand(
                "SELECT provider, consumed_at FROM auth_oauth_states WHERE state_hash = $1");
            row.Parameters.AddWithValue(Hash(issued.State));
            await using var reader = await row.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("github", reader.GetString(0));
            Assert.False(reader.IsDBNull(1));
        }
        finally
        {
            await DeleteAsync(dataSource, issued.State);
        }
    }

    [Fact]
    public async Task ExpiredStateCannotBeConsumed()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var now = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var service = new OAuthStateService(dataSource, now);
        var issued = await service.IssueAsync("google", "http://localhost:5173/oauth/callback");
        Assert.NotNull(issued);

        try
        {
            now.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));
            var result = await service.ConsumeAsync(issued.Provider, issued.State,
                issued.RedirectUri, issued.CodeVerifier);
            Assert.Equal(OAuthStateStatus.Expired, result.Status);
        }
        finally
        {
            await DeleteAsync(dataSource, issued.State);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task DeleteAsync(NpgsqlDataSource dataSource, string state)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM auth_oauth_states WHERE state_hash = $1");
        command.Parameters.AddWithValue(Hash(state));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
