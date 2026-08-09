using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ScalaAPI.Admin.Auth;

namespace ScalaAPI.Admin.Data;

public sealed record ProxyProfileInput(
    string? Name,
    string? Type,
    string? Host,
    int Port,
    string? Username,
    string? Password,
    string? Status = "active");

public sealed record ProxyProfileView(
    long Id,
    string Name,
    string Type,
    string Host,
    int Port,
    string? Username,
    bool HasPassword,
    string Status,
    int LatencyMs,
    DateTime CreatedAt);

public sealed record TlsFingerprintProfileInput(
    string? Name,
    string? Ja3Hash,
    string? Ja4Hash,
    string? CipherSuites,
    string? Status = "active");

public sealed record TlsFingerprintProfileView(
    long Id,
    string Name,
    string? Ja3Hash,
    string? Ja4Hash,
    string? CipherSuites,
    string Status,
    DateTime CreatedAt);

public enum NetworkProfileStatus
{
    Created,
    Updated,
    Deleted,
    NotFound,
    Invalid,
}

public sealed record NetworkProfileResult(
    NetworkProfileStatus Status,
    long? Id = null,
    string? Error = null);

public sealed record ProxyTestResult(
    NetworkProfileStatus Status,
    string? Health,
    int? LatencyMs,
    string? Error);

public sealed class NetworkProfileStore(
    NpgsqlDataSource dataSource,
    SecretProtector protector)
{
    private static readonly Regex Ja3Pattern = new(
        "^[0-9a-fA-F]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ja4Pattern = new(
        "^[A-Za-z0-9_-]{4,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<ProxyProfileView>> ListProxiesAsync(
        int page, int size, CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 10_000);
        size = Math.Clamp(size, 1, 100);
        await using var command = dataSource.CreateCommand("""
            SELECT id, name, type, host, port, username,
                   (password IS NOT NULL AND password <> ''), status,
                   latency_ms, created_at
            FROM proxies
            ORDER BY created_at DESC, id DESC
            OFFSET $1 LIMIT $2
            """);
        command.Parameters.AddWithValue((page - 1) * size);
        command.Parameters.AddWithValue(size);
        var items = new List<ProxyProfileView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadProxy(reader));
        return items;
    }

    public async Task<NetworkProfileResult> CreateProxyAsync(
        long actorId,
        ProxyProfileInput input,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (!TryNormalizeProxy(input, out var normalized, out var error))
            return new(NetworkProfileStatus.Invalid, Error: error);
        var encryptedPassword = string.IsNullOrEmpty(normalized.Password)
            ? null : protector.Protect(normalized.Password);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO proxies(name, type, host, port, username, password, status)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            RETURNING id
            """;
        AddProxyParameters(insert, normalized, encryptedPassword);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        await WriteAuditAsync(connection, transaction, actorId,
            "proxy.created", "proxy", id, clientIp, new
            {
                normalized.Name, normalized.Type, normalized.Host,
                normalized.Port, normalized.Username,
                has_password = encryptedPassword is not null,
            }, ct);
        await transaction.CommitAsync(ct);
        return new(NetworkProfileStatus.Created, id);
    }

    public async Task<NetworkProfileResult> UpdateProxyAsync(
        long actorId,
        long id,
        ProxyProfileInput input,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (!TryNormalizeProxy(input, out var normalized, out var error))
            return new(NetworkProfileStatus.Invalid, Error: error);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var current = connection.CreateCommand();
        current.Transaction = transaction;
        current.CommandText = "SELECT password FROM proxies WHERE id = $1 FOR UPDATE";
        current.Parameters.AddWithValue(id);
        await using var currentReader = await current.ExecuteReaderAsync(ct);
        if (!await currentReader.ReadAsync(ct))
        {
            await currentReader.CloseAsync();
            await transaction.RollbackAsync(ct);
            return new(NetworkProfileStatus.NotFound, id);
        }
        var currentPassword = currentReader.IsDBNull(0) ? null : currentReader.GetString(0);
        await currentReader.CloseAsync();

        var encryptedPassword = input.Password is null
            ? currentPassword
            : string.IsNullOrEmpty(normalized.Password)
                ? null : protector.Protect(normalized.Password);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE proxies
            SET name = $1, type = $2, host = $3, port = $4, username = $5,
                password = $6, status = $7
            WHERE id = $8
            """;
        AddProxyParameters(update, normalized, encryptedPassword);
        update.Parameters.AddWithValue(id);
        await update.ExecuteNonQueryAsync(ct);
        await WriteAuditAsync(connection, transaction, actorId,
            "proxy.updated", "proxy", id, clientIp, new
            {
                normalized.Name, normalized.Type, normalized.Host,
                normalized.Port, normalized.Username,
                password_changed = input.Password is not null,
                has_password = encryptedPassword is not null,
            }, ct);
        await transaction.CommitAsync(ct);
        return new(NetworkProfileStatus.Updated, id);
    }

    public async Task<NetworkProfileResult> DeleteProxyAsync(
        long actorId, long id, string? clientIp, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM proxies WHERE id = $1";
        delete.Parameters.AddWithValue(id);
        if (await delete.ExecuteNonQueryAsync(ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return new(NetworkProfileStatus.NotFound, id);
        }
        await WriteAuditAsync(connection, transaction, actorId,
            "proxy.deleted", "proxy", id, clientIp, new { }, ct);
        await transaction.CommitAsync(ct);
        return new(NetworkProfileStatus.Deleted, id);
    }

    public async Task<ProxyTestResult> TestProxyAsync(
        long actorId, long id, string? clientIp, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT type, host, port, username, password
            FROM proxies WHERE id = $1
            """;
        query.Parameters.AddWithValue(id);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new(NetworkProfileStatus.NotFound, null, null, "proxy_not_found");
        var type = reader.GetString(0);
        var host = reader.GetString(1);
        var port = reader.GetInt32(2);
        var username = reader.IsDBNull(3) ? null : reader.GetString(3);
        var encryptedPassword = reader.IsDBNull(4) ? null : reader.GetString(4);
        await reader.CloseAsync();

        if (type == "socks5")
            return new(NetworkProfileStatus.Invalid, "unsupported", null,
                "socks5_test_requires_provider_adapter");

        var stopwatch = Stopwatch.StartNew();
        string health;
        string? error = null;
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"{type}://{FormatHost(host)}:{port}"),
                UseProxy = true,
            };
            if (!string.IsNullOrWhiteSpace(username) && encryptedPassword is not null)
                handler.Proxy.Credentials = new NetworkCredential(
                    username, protector.Unprotect(encryptedPassword));
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            using var response = await client.GetAsync("https://httpbin.org/ip", ct);
            health = response.IsSuccessStatusCode ? "healthy" : "degraded";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            health = "unreachable";
            error = "proxy_probe_failed";
        }
        stopwatch.Stop();
        var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE proxies SET latency_ms = $1, status = $2 WHERE id = $3";
        update.Parameters.AddWithValue(latency);
        update.Parameters.AddWithValue(health);
        update.Parameters.AddWithValue(id);
        await update.ExecuteNonQueryAsync(ct);
        await WriteAuditAsync(connection, transaction, actorId,
            "proxy.tested", "proxy", id, clientIp, new { health, latency_ms = latency }, ct);
        await transaction.CommitAsync(ct);
        return new(NetworkProfileStatus.Updated, health, latency, error);
    }

    public async Task<IReadOnlyList<TlsFingerprintProfileView>> ListTlsAsync(
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, name, ja3_hash, ja4_hash, cipher_suites, status, created_at
            FROM tls_fingerprint_profiles
            ORDER BY created_at DESC, id DESC
            """);
        var items = new List<TlsFingerprintProfileView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadTls(reader));
        return items;
    }

    public async Task<NetworkProfileResult> CreateTlsAsync(
        long actorId,
        TlsFingerprintProfileInput input,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (!TryNormalizeTls(input, out var normalized, out var error))
            return new(NetworkProfileStatus.Invalid, Error: error);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tls_fingerprint_profiles(name, ja3_hash, ja4_hash, cipher_suites, status)
            VALUES ($1, $2, $3, $4, $5)
            RETURNING id
            """;
        AddTlsParameters(insert, normalized);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        await WriteAuditAsync(connection, transaction, actorId,
            "tls_profile.created", "tls_fingerprint_profile", id, clientIp,
            new { normalized.Name, normalized.Ja3Hash, normalized.Ja4Hash }, ct);
        await transaction.CommitAsync(ct);
        return new(NetworkProfileStatus.Created, id);
    }

    public async Task<NetworkProfileResult> UpdateTlsAsync(
        long actorId,
        long id,
        TlsFingerprintProfileInput input,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (!TryNormalizeTls(input, out var normalized, out var error))
            return new(NetworkProfileStatus.Invalid, Error: error);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE tls_fingerprint_profiles
            SET name = $1, ja3_hash = $2, ja4_hash = $3, cipher_suites = $4, status = $5
            WHERE id = $6
            """;
        AddTlsParameters(update, normalized);
        update.Parameters.AddWithValue(id);
        if (await update.ExecuteNonQueryAsync(ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return new(NetworkProfileStatus.NotFound, id);
        }
        await WriteAuditAsync(connection, transaction, actorId,
            "tls_profile.updated", "tls_fingerprint_profile", id, clientIp,
            new { normalized.Name, normalized.Ja3Hash, normalized.Ja4Hash }, ct);
        await transaction.CommitAsync(ct);
        return new(NetworkProfileStatus.Updated, id);
    }

    public async Task<NetworkProfileResult> DeleteTlsAsync(
        long actorId, long id, string? clientIp, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM tls_fingerprint_profiles WHERE id = $1";
        delete.Parameters.AddWithValue(id);
        if (await delete.ExecuteNonQueryAsync(ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return new(NetworkProfileStatus.NotFound, id);
        }
        await WriteAuditAsync(connection, transaction, actorId,
            "tls_profile.deleted", "tls_fingerprint_profile", id, clientIp, new { }, ct);
        await transaction.CommitAsync(ct);
        return new(NetworkProfileStatus.Deleted, id);
    }

    private static ProxyProfileView ReadProxy(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetBoolean(6), reader.GetString(7), reader.GetInt32(8), reader.GetDateTime(9));

    private static TlsFingerprintProfileView ReadTls(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
        reader.GetDateTime(6));

    private static void AddProxyParameters(
        NpgsqlCommand command, NormalizedProxy input, string? encryptedPassword)
    {
        command.Parameters.AddWithValue(input.Name);
        command.Parameters.AddWithValue(input.Type);
        command.Parameters.AddWithValue(input.Host);
        command.Parameters.AddWithValue(input.Port);
        command.Parameters.AddWithValue((object?)input.Username ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)encryptedPassword ?? DBNull.Value);
        command.Parameters.AddWithValue(input.Status);
    }

    private static void AddTlsParameters(
        NpgsqlCommand command, NormalizedTls input)
    {
        command.Parameters.AddWithValue(input.Name);
        command.Parameters.AddWithValue((object?)input.Ja3Hash ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)input.Ja4Hash ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)input.CipherSuites ?? DBNull.Value);
        command.Parameters.AddWithValue(input.Status);
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long actorId,
        string action,
        string resourceType,
        long resourceId,
        string? clientIp,
        object details,
        CancellationToken ct)
    {
        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(
                user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, $2, $3, $4, $5, $6)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(action);
        audit.Parameters.AddWithValue(resourceType);
        audit.Parameters.AddWithValue(resourceId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue(JsonSerializer.Serialize(details));
        audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync(ct);
    }

    private static bool TryNormalizeProxy(
        ProxyProfileInput input, out NormalizedProxy normalized, out string? error)
    {
        var name = input.Name?.Trim() ?? "";
        var type = input.Type?.Trim().ToLowerInvariant() ?? "";
        var host = input.Host?.Trim() ?? "";
        var username = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();
        var password = input.Password;
        var status = input.Status?.Trim().ToLowerInvariant() ?? "active";
        normalized = new(name, type, host, input.Port, username, password, status);
        error = null;
        if (name is not { Length: >= 1 and <= 120 } || HasControl(name))
            error = "name must be 1-120 characters";
        else if (type is not ("http" or "https" or "socks5"))
            error = "type must be http, https, or socks5";
        else if (host is not { Length: >= 1 and <= 253 } || HasControl(host)
            || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            error = "host must be a valid DNS name or IP address";
        else if (input.Port is < 1 or > 65_535)
            error = "port must be between 1 and 65535";
        else if (username is not null && (username.Length > 200 || HasControl(username)))
            error = "username is too long or contains control characters";
        else if (password is not null && (password.Length > 2_000 || HasControl(password)))
            error = "password is too long or contains control characters";
        else if (status is not ("active" or "disabled"))
            error = "status must be active or disabled";
        return error is null;
    }

    private static bool TryNormalizeTls(
        TlsFingerprintProfileInput input, out NormalizedTls normalized, out string? error)
    {
        var name = input.Name?.Trim() ?? "";
        var ja3 = NormalizeOptional(input.Ja3Hash);
        var ja4 = NormalizeOptional(input.Ja4Hash);
        var ciphers = NormalizeOptional(input.CipherSuites);
        var status = input.Status?.Trim().ToLowerInvariant() ?? "active";
        normalized = new(name, ja3, ja4, ciphers, status);
        error = null;
        if (name is not { Length: >= 1 and <= 120 } || HasControl(name))
            error = "name must be 1-120 characters";
        else if (ja3 is not null && !Ja3Pattern.IsMatch(ja3))
            error = "ja3_hash must be 32 hexadecimal characters";
        else if (ja4 is not null && !Ja4Pattern.IsMatch(ja4))
            error = "ja4_hash has an invalid format";
        else if (ciphers is not null && (ciphers.Length > 2_000 || HasControl(ciphers)))
            error = "cipher_suites is too long or contains control characters";
        else if (status is not ("active" or "disabled"))
            error = "status must be active or disabled";
        return error is null;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasControl(string value) => value.Any(char.IsControl);

    private static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]" : host;

    private sealed record NormalizedProxy(
        string Name, string Type, string Host, int Port, string? Username,
        string? Password, string Status);

    private sealed record NormalizedTls(
        string Name, string? Ja3Hash, string? Ja4Hash, string? CipherSuites,
        string Status);
}
