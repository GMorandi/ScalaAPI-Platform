using System.Security.Cryptography;
using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class PasskeyStoreTests
{
    [Fact]
    public async Task PasskeyChallengesAreOneShotAndCredentialCountersAreMonotonic()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var userId = 9_950_000L + Random.Shared.Next(1, 40_000);
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var userHandle = RandomNumberGenerator.GetBytes(16);
        var publicKey = RandomNumberGenerator.GetBytes(65);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PasskeyStore(dataSource);
        Guid challengeId = default;
        try
        {
            challengeId = await store.CreateChallengeAsync(userId, "registration",
                "{\"challenge\":\"test\"}", DateTime.UtcNow.AddMinutes(5));
            var challenge = await store.GetChallengeAsync(
                challengeId, userId, "registration");
            Assert.NotNull(challenge);
            Assert.Equal("test", System.Text.Json.JsonDocument.Parse(
                challenge!.OptionsJson).RootElement.GetProperty("challenge").GetString());
            Assert.True(await store.CompleteRegistrationAsync(
                challengeId, 1, userId, credentialId, userHandle,
                publicKey, 4, "Test passkey", "127.0.0.1"));
            Assert.False(await store.CompleteRegistrationAsync(
                challengeId, 1, userId, credentialId, userHandle,
                publicKey, 4, "Test passkey", "127.0.0.1"));
            var credential = await store.GetCredentialAsync(credentialId);
            Assert.NotNull(credential);
            Assert.Equal(4u, credential!.SignatureCounter);
            Assert.Single(await store.ListCredentialsAsync(userId));
            Assert.True(await store.UpdateCounterAsync(credentialId, 5));
            Assert.False(await store.UpdateCounterAsync(credentialId, 4));
            Assert.True(await store.DeleteCredentialAsync(1, userId, credentialId,
                "127.0.0.1"));
            Assert.Null(await store.GetCredentialAsync(credentialId));
        }
        finally
        {
            foreach (var (sql, value) in new[]
            {
                ("DELETE FROM passkey_challenges WHERE challenge_id = $1", (object)challengeId),
                ("DELETE FROM passkey_credentials WHERE credential_id = $1", (object)credentialId),
                ("DELETE FROM audit_logs WHERE user_id = $1 AND action IN ('passkey.registered', 'passkey.revoked')", (object)1L),
            })
            {
                await using var cleanup = dataSource.CreateCommand(sql);
                cleanup.Parameters.AddWithValue(value);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
