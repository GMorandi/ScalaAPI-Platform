using Npgsql;

namespace ScalaAPI.Admin.Auth;

public sealed record EmailDomainQuotaResult(bool Allowed, int CurrentCount, int Limit, string Domain);

public sealed class EmailDomainQuotaService(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null)
{
    private const int DefaultDomainDailyLimit = 10;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<EmailDomainQuotaResult> CheckAsync(string? email, int? limitOverride = null, CancellationToken ct = default)
    {
        var domain = ExtractDomain(email);
        if (domain is null) return new EmailDomainQuotaResult(true, 0, int.MaxValue, "");
        var limit = limitOverride ?? DefaultDomainDailyLimit;
        var today = clock.GetUtcNow().UtcDateTime.Date;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count FROM email_domain_registration_quota WHERE domain = $1 AND quota_date = $2";
        command.Parameters.AddWithValue(domain);
        command.Parameters.AddWithValue(today);
        var value = await command.ExecuteScalarAsync(ct);
        var current = value is int c ? c : 0;
        return new EmailDomainQuotaResult(current < limit, current, limit, domain);
    }

    public async Task<EmailDomainQuotaResult> TryIncrementAsync(string? email, int? limitOverride = null, CancellationToken ct = default)
    {
        var domain = ExtractDomain(email);
        if (domain is null) return new EmailDomainQuotaResult(true, 0, int.MaxValue, "");
        var limit = limitOverride ?? DefaultDomainDailyLimit;
        var today = clock.GetUtcNow().UtcDateTime.Date;
        var advisoryKey = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{domain}:{today:yyyy-MM-dd}")), 0);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var lockCmd = connection.CreateCommand()) { lockCmd.CommandText = "SELECT pg_advisory_xact_lock($1)"; lockCmd.Parameters.AddWithValue(advisoryKey); await lockCmd.ExecuteNonQueryAsync(ct); }
        await using (var ensureCmd = connection.CreateCommand()) { ensureCmd.CommandText = "INSERT INTO email_domain_registration_quota (domain, quota_date, count, updated_at) VALUES ($1, $2, 0, now()) ON CONFLICT (domain, quota_date) DO NOTHING"; ensureCmd.Parameters.AddWithValue(domain); ensureCmd.Parameters.AddWithValue(today); await ensureCmd.ExecuteNonQueryAsync(ct); }
        int current;
        await using (var readCmd = connection.CreateCommand()) { readCmd.CommandText = "SELECT count FROM email_domain_registration_quota WHERE domain = $1 AND quota_date = $2"; readCmd.Parameters.AddWithValue(domain); readCmd.Parameters.AddWithValue(today); var value = await readCmd.ExecuteScalarAsync(ct); current = value is int c ? c : 0; }
        if (current >= limit) return new EmailDomainQuotaResult(false, current, limit, domain);
        await using (var incrCmd = connection.CreateCommand()) { incrCmd.CommandText = "UPDATE email_domain_registration_quota SET count = count + 1, updated_at = now() WHERE domain = $1 AND quota_date = $2"; incrCmd.Parameters.AddWithValue(domain); incrCmd.Parameters.AddWithValue(today); await incrCmd.ExecuteNonQueryAsync(ct); }
        return new EmailDomainQuotaResult(true, current + 1, limit, domain);
    }

    public async Task<IReadOnlyList<(string Domain, int Count, int Limit)>> ListAsync(CancellationToken ct)
    {
        var results = new List<(string, int, int)>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT domain, count, (SELECT daily_limit FROM captcha_config LIMIT 1) FROM email_domain_registration_quota WHERE quota_date = CURRENT_DATE ORDER BY domain";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetInt32(1), 10));
        return results;
    }

    public async Task SetDomainLimitAsync(string domain, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO email_domain_registration_quota (domain, quota_date, count, updated_at) VALUES ($1, CURRENT_DATE, 0, now()) ON CONFLICT (domain, quota_date) DO NOTHING";
        cmd.Parameters.AddWithValue(domain);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var atIndex = email.LastIndexOf('@');
        if (atIndex < 1 || atIndex >= email.Length - 1) return null;
        return email[(atIndex + 1)..].Trim().ToLowerInvariant();
    }
}
