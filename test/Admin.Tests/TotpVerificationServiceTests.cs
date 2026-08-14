using Microsoft.Extensions.Configuration;
using Npgsql;
using OtpNet;
using ScalaAPI.Admin.Auth;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class TotpVerificationServiceTests
{
    [Fact]
    public async Task EnableRejectsTimeStepReplayAndConsumesBackupCodesOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var protector = CreateProtector();
        var now = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var (userId, secret) = await InsertUserAsync(dataSource, protector);
        var service = new TotpVerificationService(dataSource, protector, now);
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var code = totp.ComputeTotp(now.GetUtcNow().UtcDateTime);
        var backup = "0123456789abcdef0123";
        var hashes = Enumerable.Range(0, 10)
            .Select(index => BCrypt.Net.BCrypt.HashPassword(index == 0 ? backup : $"unused-{index}"))
            .ToArray();

        try
        {
            var enabled = await service.EnableAsync(userId, code, hashes);
            Assert.True(enabled.Accepted);

            var replay = await service.VerifyAsync(userId, code, allowBackupCodes: false);
            Assert.Equal(TotpVerificationStatus.Replayed, replay.Status);

            var disableWithBackup = await service.DisableAsync(userId, backup);
            Assert.Equal(TotpVerificationStatus.Invalid, disableWithBackup.Status);

            var consumed = await service.VerifyAsync(userId, backup, allowBackupCodes: true);
            Assert.True(consumed.Accepted);
            Assert.True(consumed.UsedBackupCode);

            var reused = await service.VerifyAsync(userId, backup, allowBackupCodes: true);
            Assert.Equal(TotpVerificationStatus.Invalid, reused.Status);

            now.Advance(TimeSpan.FromSeconds(30));
            var nextCode = totp.ComputeTotp(now.GetUtcNow().UtcDateTime);
            var disabled = await service.DisableAsync(userId, nextCode);
            Assert.True(disabled.Accepted);

            await using var verify = dataSource.CreateCommand(
                "SELECT totp_enabled, totp_secret, totp_backup_codes FROM user_accounts WHERE id = $1");
            verify.Parameters.AddWithValue(userId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    [Fact]
    public async Task FailedCodesLockAcrossServiceInstancesAndRecoverAfterLockout()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var protector = CreateProtector();
        var now = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var (userId, secret) = await InsertUserAsync(dataSource, protector, enabled: true);
        var serviceOne = new TotpVerificationService(dataSource, protector, now);
        var serviceTwo = new TotpVerificationService(dataSource, protector, now);
        var code = new Totp(Base32Encoding.ToBytes(secret))
            .ComputeTotp(now.GetUtcNow().UtcDateTime);

        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
                Assert.Equal(TotpVerificationStatus.Invalid,
                    (await serviceOne.VerifyAsync(userId, "000000", false)).Status);

            var locked = await serviceOne.VerifyAsync(userId, "000000", false);
            Assert.Equal(TotpVerificationStatus.Locked, locked.Status);

            var otherInstance = await serviceTwo.VerifyAsync(userId, code, false);
            Assert.Equal(TotpVerificationStatus.Locked, otherInstance.Status);

            now.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));
            var recovered = await serviceTwo.VerifyAsync(userId,
                new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(now.GetUtcNow().UtcDateTime),
                false);
            Assert.True(recovered.Accepted);
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    private static SecretProtector CreateProtector()
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:MasterKey"] = key,
            })
            .Build();
        return new SecretProtector(configuration);
    }

    private static async Task<(long UserId, string Secret)> InsertUserAsync(
        NpgsqlDataSource dataSource, SecretProtector protector, bool enabled = false)
    {
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        await using var command = dataSource.CreateCommand("""
            INSERT INTO user_accounts(email, totp_secret, totp_enabled)
            VALUES ($1, $2, $3)
            RETURNING id
            """);
        command.Parameters.AddWithValue($"totp-{Guid.NewGuid():N}@scalaapi.test");
        command.Parameters.AddWithValue(protector.Protect(secret));
        command.Parameters.AddWithValue(enabled);
        return (Convert.ToInt64(await command.ExecuteScalarAsync()), secret);
    }

    private static async Task DeleteUserAsync(NpgsqlDataSource dataSource, long userId)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM user_accounts WHERE id = $1");
        command.Parameters.AddWithValue(userId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
