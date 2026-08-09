using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Npgsql;

namespace ScalaAPI.Admin.Auth;

public sealed record EmailMessage(string Recipient, string Subject, string TextBody,
    string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed class SmtpEmailSender(IConfiguration configuration,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var provider = configuration["Email:Provider"]?.Trim().ToLowerInvariant() ?? "smtp";
        if (provider == "filesystem")
        {
            await CaptureAsync(message, ct);
            return;
        }

        if (provider != "smtp")
            throw new InvalidOperationException("Email:Provider must be smtp or filesystem");

        var host = configuration["Smtp:Host"]?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("SMTP is not configured");

        var port = ParsePort(configuration["Smtp:Port"]);
        var secureSocketOptions = ParseSecurity(configuration["Smtp:SecureSocketOptions"]);
        var from = configuration["Smtp:From"]?.Trim() ?? "noreply@example.invalid";
        var username = configuration["Smtp:Username"]?.Trim();
        var password = configuration["Smtp:Password"] ?? string.Empty;

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(from));
        mime.To.Add(MailboxAddress.Parse(message.Recipient));
        mime.Subject = message.Subject;
        var body = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
        };
        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port, secureSocketOptions, ct);
        if (!string.IsNullOrWhiteSpace(username))
            await client.AuthenticateAsync(username, password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("Delivered notification email {Kind} to {Recipient}",
            message.Subject, RedactRecipient(message.Recipient));
    }

    private async Task CaptureAsync(EmailMessage message, CancellationToken ct)
    {
        var directory = configuration["Email:CaptureDirectory"]?.Trim();
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Email:CaptureDirectory is required for filesystem delivery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml");
        var content = $"To: {message.Recipient}\nSubject: {message.Subject}\n\n{message.TextBody}\n\n{message.HtmlBody}";
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
        logger.LogInformation("Captured notification email at {Path}", path);
    }

    private static int ParsePort(string? value) =>
        int.TryParse(value, out var port) && port is >= 1 and <= 65535 ? port : 587;

    private static SecureSocketOptions ParseSecurity(string? value) =>
        Enum.TryParse<SecureSocketOptions>(value, true, out var parsed)
            ? parsed : SecureSocketOptions.StartTls;

    private static string RedactRecipient(string recipient)
    {
        var at = recipient.IndexOf('@');
        return at <= 1 ? "[redacted]" : $"{recipient[0]}***{recipient[at..]}";
    }
}

public static class EmailOutboxStore
{
    public static async Task CancelPendingAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string recipient, string kind,
        CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE email_delivery_outbox
            SET status = 'failed', attempts = 10, available_at = now() + interval '100 years',
                claimed_until = NULL,
                last_error = 'superseded by a newer authentication token'
            WHERE recipient = $1 AND kind = $2 AND status IN ('pending', 'sending')
            """;
        command.Parameters.AddWithValue(recipient);
        command.Parameters.AddWithValue(kind);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task EnqueueAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string messageKey, string recipient, string kind,
        string tokenCiphertext, DateTime expiresAt, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO email_delivery_outbox(
                message_key, recipient, kind, token_ciphertext, expires_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (message_key) DO NOTHING
            """;
        command.Parameters.AddWithValue(messageKey);
        command.Parameters.AddWithValue(recipient);
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(tokenCiphertext);
        command.Parameters.AddWithValue(expiresAt);
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class EmailDeliveryWorker(
    NpgsqlDataSource dataSource,
    SecretProtector protector,
    IEmailSender sender,
    IConfiguration configuration,
    ILogger<EmailDeliveryWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(ParseInterval(configuration["Email:PollSeconds"]));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessPendingOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Email delivery polling failed");
            }
        }
    }

    public async Task<int> ProcessPendingOnceAsync(CancellationToken ct = default)
    {
        await ExpireRowsAsync(ct);
        var records = await ClaimAsync(ct);
        foreach (var record in records)
        {
            try
            {
                var token = protector.Unprotect(record.TokenCiphertext);
                var message = BuildMessage(record.Kind, record.Recipient, token,
                    record.ExpiresAt, configuration["Email:PublicBaseUrl"]);
                await sender.SendAsync(message, ct);
                await MarkSentAsync(record.Id, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                || !ct.IsCancellationRequested)
            {
                await MarkFailureAsync(record, exception, ct);
                logger.LogWarning(exception, "Email delivery attempt failed for outbox row {Id}",
                    record.Id);
            }
        }
        return records.Count;
    }

    private async Task ExpireRowsAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE email_delivery_outbox
            SET status = 'failed', claimed_until = NULL, last_error = 'authentication token expired'
            WHERE status IN ('pending', 'sending') AND expires_at <= now()
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<OutboxRecord>> ClaimAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var records = new List<OutboxRecord>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, recipient, kind, token_ciphertext, expires_at, attempts
                FROM email_delivery_outbox
                WHERE attempts < $1 AND expires_at > now()
                  AND ((status IN ('pending', 'failed') AND available_at <= now())
                       OR (status = 'sending' AND claimed_until <= now()))
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT $2
                """;
            select.Parameters.AddWithValue(MaxAttempts);
            select.Parameters.AddWithValue(BatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                records.Add(new OutboxRecord(reader.GetInt64(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetDateTime(4),
                    reader.GetInt32(5) + 1));
            }
        }

        foreach (var record in records)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE email_delivery_outbox
                SET status = 'sending', attempts = attempts + 1,
                    claimed_until = now() + interval '5 minutes', last_error = NULL
                WHERE id = $1
                """;
            update.Parameters.AddWithValue(record.Id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return records;
    }

    private async Task MarkSentAsync(long id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE email_delivery_outbox
            SET status = 'sent', sent_at = now(), claimed_until = NULL
            WHERE id = $1 AND status = 'sending'
            """);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkFailureAsync(OutboxRecord record, Exception exception,
        CancellationToken ct)
    {
        var delaySeconds = Math.Min(300, 1 << Math.Min(record.Attempts, 8));
        var error = exception.Message.Length > 500
            ? exception.Message[..500] : exception.Message;
        await using var command = dataSource.CreateCommand("""
            UPDATE email_delivery_outbox
            SET status = CASE WHEN attempts >= $2 THEN 'failed' ELSE 'pending' END,
                available_at = now() + make_interval(secs => $3),
                claimed_until = NULL, last_error = $4
            WHERE id = $1 AND status = 'sending'
            """);
        command.Parameters.AddWithValue(record.Id);
        command.Parameters.AddWithValue(MaxAttempts);
        command.Parameters.AddWithValue(delaySeconds);
        command.Parameters.AddWithValue(error);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static EmailMessage BuildMessage(string kind, string recipient, string token,
        DateTime expiresAt, string? configuredBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? "http://localhost:5173" : configuredBaseUrl.TrimEnd('/');
        var encoded = Uri.EscapeDataString(token);
        var (subject, path, action) = kind switch
        {
            "password_reset" => ("Reset your ScalaAPI password", "/recover", "reset your password"),
            "email_verification" => ("Verify your ScalaAPI email", "/verify-email", "verify your email"),
            _ => throw new InvalidOperationException($"Unsupported email kind: {kind}"),
        };
        var link = $"{baseUrl}{path}?token={encoded}";
        var expiry = expiresAt.ToUniversalTime().ToString("u");
        return new EmailMessage(recipient, subject,
            $"Use this link to {action}: {link}\nIt expires at {expiry} UTC.",
            $"<p>Use this link to {action}:</p><p><a href=\"{link}\">{link}</a></p><p>It expires at {expiry} UTC.</p>");
    }

    private static int ParseInterval(string? value) =>
        int.TryParse(value, out var seconds) && seconds is >= 1 and <= 300 ? seconds : 5;

    private sealed record OutboxRecord(long Id, string Recipient, string Kind,
        string TokenCiphertext, DateTime ExpiresAt, int Attempts);
}
