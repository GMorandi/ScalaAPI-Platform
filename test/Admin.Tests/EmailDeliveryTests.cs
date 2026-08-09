using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Admin.Auth;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class EmailDeliveryTests
{
    [Fact]
    public async Task AuthTokensQueueEncryptedOutboxMessagesAndWorkerDeliversOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var protector = CreateProtector();
        var userId = 9_970_000L + Random.Shared.Next(1, 20_000);
        var email = $"delivery-{userId}@example.test";
        await InsertUserAsync(dataSource, userId, email);
        var sender = new RecordingSender();
        var worker = new EmailDeliveryWorker(dataSource, protector, sender,
            CreateConfiguration(), NullLogger<EmailDeliveryWorker>.Instance);
        try
        {
            var service = new PasswordResetService(dataSource, protector,
                NullLogger<PasswordResetService>.Instance);
            var issued = await service.IssueAsync(email);
            Assert.NotNull(issued);

            await using (var command = dataSource.CreateCommand("""
                SELECT recipient, kind, token_ciphertext, status
                FROM email_delivery_outbox
                WHERE message_key = $1
                """))
            {
                command.Parameters.AddWithValue($"password-reset:{Hash(issued!.Token)}");
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(email, reader.GetString(0));
                Assert.Equal("password_reset", reader.GetString(1));
                var ciphertext = reader.GetString(2);
                Assert.NotEqual(issued.Token, ciphertext);
                Assert.Equal(issued.Token, protector.Unprotect(ciphertext));
                Assert.Equal("pending", reader.GetString(3));
            }

            Assert.Equal(1, await worker.ProcessPendingOnceAsync());
            var message = Assert.Single(sender.Messages);
            Assert.Equal(email, message.Recipient);
            Assert.Contains(issued.Token, message.TextBody, StringComparison.Ordinal);
            Assert.Contains("/recover?token=", message.TextBody, StringComparison.Ordinal);
            Assert.Equal("sent", await ScalarAsync<string>(dataSource,
                "SELECT status FROM email_delivery_outbox WHERE message_key = $1",
                $"password-reset:{Hash(issued.Token)}"));
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    [Fact]
    public async Task FailedDeliveryRemainsRetryableAndThenReachesSent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var protector = CreateProtector();
        var userId = 9_950_000L + Random.Shared.Next(1, 20_000);
        var email = $"retry-{userId}@example.test";
        await InsertUserAsync(dataSource, userId, email);
        var sender = new RecordingSender { FailuresRemaining = 1 };
        var worker = new EmailDeliveryWorker(dataSource, protector, sender,
            CreateConfiguration(), NullLogger<EmailDeliveryWorker>.Instance);
        try
        {
            var service = new EmailVerificationService(dataSource, protector,
                NullLogger<EmailVerificationService>.Instance);
            var issued = await service.IssueAsync(email);
            Assert.NotNull(issued);

            Assert.Equal(1, await worker.ProcessPendingOnceAsync());
            Assert.Equal("pending", await ScalarAsync<string>(dataSource,
                "SELECT status FROM email_delivery_outbox WHERE message_key = $1",
                $"email-verification:{Hash(issued!.Token)}"));
            Assert.Equal(1L, await ScalarAsync<long>(dataSource,
                "SELECT attempts FROM email_delivery_outbox WHERE message_key = $1",
                $"email-verification:{Hash(issued.Token)}"));

            await ExecuteAsync(dataSource, """
                UPDATE email_delivery_outbox
                SET available_at = now()
                WHERE message_key = $1
                """, $"email-verification:{Hash(issued.Token)}");
            Assert.Equal(1, await worker.ProcessPendingOnceAsync());
            Assert.Equal("sent", await ScalarAsync<string>(dataSource,
                "SELECT status FROM email_delivery_outbox WHERE message_key = $1",
                $"email-verification:{Hash(issued.Token)}"));
            Assert.Single(sender.Messages);
            Assert.Contains("/verify-email?token=", sender.Messages[0].TextBody,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    [Fact]
    public async Task NewRequestSupersedesAnOlderPendingAuthenticationEmail()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var protector = CreateProtector();
        var userId = 9_940_000L + Random.Shared.Next(1, 20_000);
        var email = $"supersede-{userId}@example.test";
        await InsertUserAsync(dataSource, userId, email);
        var sender = new RecordingSender();
        var worker = new EmailDeliveryWorker(dataSource, protector, sender,
            CreateConfiguration(), NullLogger<EmailDeliveryWorker>.Instance);
        try
        {
            var service = new PasswordResetService(dataSource, protector,
                NullLogger<PasswordResetService>.Instance);
            var first = await service.IssueAsync(email);
            var second = await service.IssueAsync(email);
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first!.Token, second!.Token);

            await using (var command = dataSource.CreateCommand("""
                SELECT status, attempts, last_error
                FROM email_delivery_outbox
                WHERE recipient = $1 AND kind = 'password_reset'
                ORDER BY id
                """))
            {
                command.Parameters.AddWithValue(email);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("failed", reader.GetString(0));
                Assert.Equal(10, reader.GetInt32(1));
                Assert.Contains("superseded", reader.GetString(2), StringComparison.Ordinal);
                Assert.True(await reader.ReadAsync());
                Assert.Equal("pending", reader.GetString(0));
            }

            Assert.Equal(1, await worker.ProcessPendingOnceAsync());
            Assert.Single(sender.Messages);
            Assert.Contains(second.Token, sender.Messages[0].TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain(first.Token, sender.Messages[0].TextBody, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteUserAsync(dataSource, userId);
        }
    }

    private static SecretProtector CreateProtector()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:MasterKey"] = key,
            }).Build();
        return new SecretProtector(configuration);
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:PublicBaseUrl"] = "https://app.scalaapi.test",
            ["Email:PollSeconds"] = "1",
        }).Build();

    private static async Task InsertUserAsync(NpgsqlDataSource dataSource, long id, string email)
    {
        await ExecuteAsync(dataSource, """
            INSERT INTO user_accounts(id, email, status, role)
            VALUES ($1, $2, 'active', 'user')
            """, id, email);
    }

    private static async Task DeleteUserAsync(NpgsqlDataSource dataSource, long id)
    {
        await ExecuteAsync(dataSource,
            "DELETE FROM email_delivery_outbox WHERE recipient = (SELECT email FROM user_accounts WHERE id = $1)", id);
        await ExecuteAsync(dataSource, "DELETE FROM password_reset_tokens WHERE user_id = $1", id);
        await ExecuteAsync(dataSource, "DELETE FROM email_verification_tokens WHERE user_id = $1", id);
        await ExecuteAsync(dataSource, "DELETE FROM user_accounts WHERE id = $1", id);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql,
        params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql,
        params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var item in values) command.Parameters.AddWithValue(item);
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value ?? throw new InvalidOperationException("Missing scalar"),
            typeof(T));
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];
        public int FailuresRemaining { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("simulated SMTP outage");
            }
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
