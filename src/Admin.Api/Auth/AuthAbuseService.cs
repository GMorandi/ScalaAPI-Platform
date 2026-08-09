using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ScalaAPI.Admin.Auth;

public sealed record AuthAbuseDecision(bool Allowed, int RetryAfterSeconds = 0);

public static class AuthInputValidation
{
    public static bool TryNormalizeEmail(string? value, out string email)
    {
        email = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.Length > 320 || candidate.Any(char.IsWhiteSpace)) return false;
        try
        {
            var parsed = new MailAddress(candidate);
            if (!string.Equals(parsed.Address, candidate, StringComparison.OrdinalIgnoreCase))
                return false;
            email = candidate.ToLowerInvariant();
            return email.Contains('@', StringComparison.Ordinal)
                && email.IndexOf('@') > 0
                && email.IndexOf('@') == email.LastIndexOf('@');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsValidPassword(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 12 and <= 256;

    public static string? NormalizeDisplayName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class AuthAbuseService(NpgsqlDataSource dataSource)
{
    public const int LoginFailureLimit = 5;
    public const int RegistrationFailureLimit = 10;
    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LoginLock = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RegistrationWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan RegistrationLock = TimeSpan.FromHours(1);

    public async Task<AuthAbuseDecision> CheckLoginAsync(
        string? normalizedEmail, string? ipAddress, CancellationToken ct = default)
    {
        var identity = await InspectAsync(Key("login-email", normalizedEmail),
            LoginFailureLimit, LoginWindow, LoginLock, ct);
        if (!identity.Allowed) return identity;
        return await InspectAsync(Key("login-ip", ipAddress),
            LoginFailureLimit * 6, LoginWindow, LoginLock, ct);
    }

    public async Task RecordLoginFailureAsync(
        string? normalizedEmail, string? ipAddress, CancellationToken ct = default)
    {
        await RecordFailureAsync(Key("login-email", normalizedEmail),
            LoginFailureLimit, LoginWindow, LoginLock, ct);
        await RecordFailureAsync(Key("login-ip", ipAddress),
            LoginFailureLimit * 6, LoginWindow, LoginLock, ct);
    }

    public async Task RecordLoginSuccessAsync(
        string? normalizedEmail, string? ipAddress, CancellationToken ct = default)
    {
        await ClearAsync(Key("login-email", normalizedEmail), ct);
        await ClearAsync(Key("login-ip", ipAddress), ct);
    }

    public Task<AuthAbuseDecision> CheckRegistrationAsync(
        string? ipAddress, CancellationToken ct = default) =>
        InspectAsync(Key("register-ip", ipAddress), RegistrationFailureLimit,
            RegistrationWindow, RegistrationLock, ct);

    public Task RecordRegistrationFailureAsync(
        string? ipAddress, CancellationToken ct = default) =>
        RecordFailureAsync(Key("register-ip", ipAddress), RegistrationFailureLimit,
            RegistrationWindow, RegistrationLock, ct);

    public Task RecordRegistrationSuccessAsync(
        string? ipAddress, CancellationToken ct = default) =>
        ClearAsync(Key("register-ip", ipAddress), ct);

    private async Task<AuthAbuseDecision> InspectAsync(string key, int limit,
        TimeSpan window, TimeSpan lockDuration, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT failure_count, window_started_at, locked_until
            FROM auth_abuse_counters
            WHERE counter_key = $1
            FOR UPDATE
            """;
        command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(ct);
            return new AuthAbuseDecision(true);
        }

        var lockedUntil = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        if (lockedUntil is null || lockedUntil <= DateTime.UtcNow)
            return new AuthAbuseDecision(true);
        return new AuthAbuseDecision(false,
            Math.Max(1, (int)Math.Ceiling((lockedUntil.Value - DateTime.UtcNow).TotalSeconds)));
    }

    private async Task RecordFailureAsync(string key, int limit,
        TimeSpan window, TimeSpan lockDuration, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT failure_count, window_started_at
            FROM auth_abuse_counters
            WHERE counter_key = $1
            FOR UPDATE
            """;
        find.Parameters.AddWithValue(key);
        await using var reader = await find.ExecuteReaderAsync(ct);
        var exists = await reader.ReadAsync(ct);
        var count = exists ? reader.GetInt32(0) : 0;
        var started = exists ? reader.GetDateTime(1) : now;
        await reader.DisposeAsync();
        if (!exists || now - started >= window)
        {
            count = 1;
            started = now;
        }
        else
        {
            count++;
        }

        var lockedUntil = count >= limit ? now.Add(lockDuration) : (DateTime?)null;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            INSERT INTO auth_abuse_counters
                (counter_key, failure_count, window_started_at, locked_until, updated_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (counter_key) DO UPDATE SET
                failure_count = EXCLUDED.failure_count,
                window_started_at = EXCLUDED.window_started_at,
                locked_until = EXCLUDED.locked_until,
                updated_at = EXCLUDED.updated_at
            """;
        update.Parameters.AddWithValue(key);
        update.Parameters.AddWithValue(count);
        update.Parameters.AddWithValue(started);
        update.Parameters.AddWithValue((object?)lockedUntil ?? DBNull.Value);
        update.Parameters.AddWithValue(now);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ClearAsync(string key, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM auth_abuse_counters WHERE counter_key = $1");
        command.Parameters.AddWithValue(key);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Key(string kind, string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return $"{kind}:{digest}";
    }
}
