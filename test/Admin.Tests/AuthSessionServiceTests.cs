using Microsoft.Extensions.Configuration;
using Npgsql;
using ScalaAPI.Admin.Auth;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class AuthSessionServiceTests
{
    [Fact]
    public async Task RefreshRotationIsSingleUseAndCreatesOneReplacement()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = CreateService(dataSource);
        var userId = await InsertUserAsync(dataSource);

        try
        {
            var issued = await service.IssueAsync(userId, $"session-{userId}@scalaapi.test", "user");
            var rotated = await service.RotateAsync(issued.RefreshToken);

            Assert.NotNull(rotated);
            Assert.NotEqual(issued.SessionId, rotated.SessionId);
            Assert.False(await service.IsActiveAsync(issued.SessionId));
            Assert.True(await service.IsActiveAsync(rotated.SessionId));

            var replay = await service.RotateAsync(issued.RefreshToken);
            Assert.Null(replay);

            await using var command = dataSource.CreateCommand("""
                SELECT revoked_at IS NOT NULL, replaced_by
                FROM auth_sessions
                WHERE session_id = $1
                """);
            command.Parameters.AddWithValue(issued.SessionId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.Equal(rotated.SessionId, reader.GetString(1));
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    [Fact]
    public async Task ConcurrentRefreshRotationAllowsOnlyOneWinner()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = CreateService(dataSource);
        var userId = await InsertUserAsync(dataSource);

        try
        {
            var issued = await service.IssueAsync(userId, $"concurrent-{userId}@scalaapi.test", "user");
            var results = await Task.WhenAll(
                service.RotateAsync(issued.RefreshToken, userAgent: "first"),
                service.RotateAsync(issued.RefreshToken, userAgent: "second"));

            Assert.Single(results, tokens => tokens is not null);
            Assert.Single(results, tokens => tokens is null);

            await using var command = dataSource.CreateCommand("""
                SELECT count(*)
                FROM auth_sessions
                WHERE user_id = $1 AND revoked_at IS NULL AND expires_at > now()
                """);
            command.Parameters.AddWithValue(userId);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    [Fact]
    public async Task RevocationAndExpiryInvalidateAccessAndRefreshPaths()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = CreateService(dataSource);
        var userId = await InsertUserAsync(dataSource);

        try
        {
            var revoked = await service.IssueAsync(userId, $"revoked-{userId}@scalaapi.test", "user");
            Assert.True(await service.RevokeSessionAsync(revoked.SessionId));
            Assert.False(await service.RevokeSessionAsync(revoked.SessionId));
            Assert.False(await service.IsActiveAsync(revoked.SessionId));
            Assert.Null(await service.RotateAsync(revoked.RefreshToken));

            var expired = await service.IssueAsync(userId, $"expired-{userId}@scalaapi.test", "user");
            await using (var expire = dataSource.CreateCommand(
                "UPDATE auth_sessions SET expires_at = now() - interval '1 second' WHERE session_id = $1"))
            {
                expire.Parameters.AddWithValue(expired.SessionId);
                await expire.ExecuteNonQueryAsync();
            }

            Assert.False(await service.IsActiveAsync(expired.SessionId));
            Assert.Null(await service.RotateAsync(expired.RefreshToken));
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    private static AuthSessionService CreateService(NpgsqlDataSource dataSource)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "auth-session-test-key-012345678901234567890123456789",
                ["Jwt:Issuer"] = "scalaapi-admin-tests",
                ["Jwt:ExpiryMinutes"] = "30",
            })
            .Build();
        return new AuthSessionService(dataSource, new JwtService(configuration),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthSessionService>.Instance);
    }

    private static async Task<long> InsertUserAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO user_accounts(email, password_hash)
            VALUES ($1, NULL)
            RETURNING id
            """);
        command.Parameters.AddWithValue($"auth-session-{Guid.NewGuid():N}@scalaapi.test");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task DeleteUserAsync(NpgsqlDataSource dataSource, long userId)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM user_accounts WHERE id = $1");
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync();
    }
}
