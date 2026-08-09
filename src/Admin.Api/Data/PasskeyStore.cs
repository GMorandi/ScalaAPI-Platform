using Npgsql;

namespace ScalaAPI.Admin.Data;

public sealed record PasskeyChallengeRecord(
    Guid ChallengeId,
    long UserId,
    string Flow,
    string OptionsJson,
    DateTime ExpiresAt);

public sealed record PasskeyCredentialRecord(
    byte[] CredentialId,
    long UserId,
    byte[] UserHandle,
    byte[] PublicKey,
    uint SignatureCounter,
    string DisplayName,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

public sealed class PasskeyStore(NpgsqlDataSource dataSource)
{
    public async Task<Guid> CreateChallengeAsync(
        long userId,
        string flow,
        string optionsJson,
        DateTime expiresAt,
        CancellationToken ct = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (flow is not ("registration" or "authentication"))
            throw new ArgumentException("Invalid passkey flow", nameof(flow));
        if (optionsJson.Length is < 2 or > 100_000)
            throw new ArgumentException("Passkey options are out of bounds", nameof(optionsJson));
        var challengeId = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO passkey_challenges(
                challenge_id, user_id, flow, options, expires_at)
            VALUES ($1, $2, $3, $4::jsonb, $5)
            """);
        command.Parameters.AddWithValue(challengeId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(flow);
        command.Parameters.AddWithValue(optionsJson);
        command.Parameters.AddWithValue(expiresAt);
        await command.ExecuteNonQueryAsync(ct);
        return challengeId;
    }

    public async Task<PasskeyChallengeRecord?> GetChallengeAsync(
        Guid challengeId,
        long userId,
        string flow,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT challenge_id, user_id, flow, options::text, expires_at
            FROM passkey_challenges
            WHERE challenge_id = $1 AND user_id = $2 AND flow = $3
              AND consumed_at IS NULL AND expires_at > now()
            """);
        command.Parameters.AddWithValue(challengeId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(flow);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new PasskeyChallengeRecord(reader.GetGuid(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetDateTime(4))
            : null;
    }

    public async Task<PasskeyChallengeRecord?> GetChallengeByIdAsync(
        Guid challengeId,
        string flow,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT challenge_id, user_id, flow, options::text, expires_at
            FROM passkey_challenges
            WHERE challenge_id = $1 AND flow = $2
              AND consumed_at IS NULL AND expires_at > now()
            """);
        command.Parameters.AddWithValue(challengeId);
        command.Parameters.AddWithValue(flow);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new PasskeyChallengeRecord(reader.GetGuid(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetDateTime(4))
            : null;
    }

    public async Task<bool> TryConsumeChallengeAsync(
        Guid challengeId,
        long userId,
        string flow,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE passkey_challenges
            SET consumed_at = now()
            WHERE challenge_id = $1 AND user_id = $2 AND flow = $3
              AND consumed_at IS NULL AND expires_at > now()
            """);
        command.Parameters.AddWithValue(challengeId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(flow);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<PasskeyCredentialRecord>> ListCredentialsAsync(
        long userId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT credential_id, user_id, user_handle, public_key,
                   signature_counter, display_name, created_at, last_used_at
            FROM passkey_credentials
            WHERE user_id = $1
            ORDER BY created_at DESC, credential_id
            """);
        command.Parameters.AddWithValue(userId);
        return await ReadCredentialsAsync(command, ct);
    }

    public async Task<PasskeyCredentialRecord?> GetCredentialAsync(
        byte[] credentialId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT credential_id, user_id, user_handle, public_key,
                   signature_counter, display_name, created_at, last_used_at
            FROM passkey_credentials
            WHERE credential_id = $1
            """);
        command.Parameters.AddWithValue(credentialId);
        var rows = await ReadCredentialsAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<bool> CredentialExistsAsync(
        byte[] credentialId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM passkey_credentials WHERE credential_id = $1)");
        command.Parameters.AddWithValue(credentialId);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task AddCredentialAsync(
        long actorId,
        long userId,
        byte[] credentialId,
        byte[] userHandle,
        byte[] publicKey,
        uint signatureCounter,
        string? displayName,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (actorId <= 0 || userId <= 0 || credentialId.Length is 0 or > 1024
            || userHandle.Length is 0 or > 128 || publicKey.Length is 0 or > 4096)
            throw new ArgumentException("Passkey credential material is out of bounds");
        displayName = string.IsNullOrWhiteSpace(displayName) ? "Passkey" : displayName.Trim();
        if (displayName.Length > 200 || displayName.Any(char.IsControl))
            throw new ArgumentException("Passkey display name is invalid", nameof(displayName));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO passkey_credentials(
                credential_id, user_id, user_handle, public_key,
                signature_counter, display_name)
            VALUES ($1, $2, $3, $4, $5, $6)
            """;
        insert.Parameters.AddWithValue(credentialId);
        insert.Parameters.AddWithValue(userId);
        insert.Parameters.AddWithValue(userHandle);
        insert.Parameters.AddWithValue(publicKey);
        insert.Parameters.AddWithValue((long)signatureCounter);
        insert.Parameters.AddWithValue(displayName);
        await insert.ExecuteNonQueryAsync(ct);

        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(
                user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, 'passkey.registered', 'passkey', $2, $3, $4)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(Convert.ToHexString(credentialId).ToLowerInvariant());
        audit.Parameters.AddWithValue("{\"user_id\":" + userId + "}");
        audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> CompleteRegistrationAsync(
        Guid challengeId,
        long actorId,
        long userId,
        byte[] credentialId,
        byte[] userHandle,
        byte[] publicKey,
        uint signatureCounter,
        string? displayName,
        string? clientIp,
        CancellationToken ct = default)
    {
        ValidateCredential(actorId, userId, credentialId, userHandle, publicKey, displayName,
            out displayName);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = """
            UPDATE passkey_challenges
            SET consumed_at = now()
            WHERE challenge_id = $1 AND user_id = $2 AND flow = 'registration'
              AND consumed_at IS NULL AND expires_at > now()
            """;
        consume.Parameters.AddWithValue(challengeId);
        consume.Parameters.AddWithValue(userId);
        if (await consume.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO passkey_credentials(
                credential_id, user_id, user_handle, public_key,
                signature_counter, display_name)
            VALUES ($1, $2, $3, $4, $5, $6)
            """;
        insert.Parameters.AddWithValue(credentialId);
        insert.Parameters.AddWithValue(userId);
        insert.Parameters.AddWithValue(userHandle);
        insert.Parameters.AddWithValue(publicKey);
        insert.Parameters.AddWithValue((long)signatureCounter);
        insert.Parameters.AddWithValue(displayName);
        await insert.ExecuteNonQueryAsync(ct);

        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(
                user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, 'passkey.registered', 'passkey', $2, $3, $4)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(Convert.ToHexString(credentialId).ToLowerInvariant());
        audit.Parameters.AddWithValue("{\"user_id\":" + userId + "}");
        audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> UpdateCounterAsync(
        byte[] credentialId,
        uint signatureCounter,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE passkey_credentials
            SET signature_counter = $2, last_used_at = now()
            WHERE credential_id = $1 AND signature_counter <= $2
            """);
        command.Parameters.AddWithValue(credentialId);
        command.Parameters.AddWithValue((long)signatureCounter);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteCredentialAsync(
        long actorId,
        long userId,
        byte[] credentialId,
        string? clientIp,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM passkey_credentials
            WHERE credential_id = $1 AND user_id = $2
            """;
        delete.Parameters.AddWithValue(credentialId);
        delete.Parameters.AddWithValue(userId);
        if (await delete.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }
        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(
                user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, 'passkey.revoked', 'passkey', $2, '{}', $3)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(Convert.ToHexString(credentialId).ToLowerInvariant());
        audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task<IReadOnlyList<PasskeyCredentialRecord>> ReadCredentialsAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        var items = new List<PasskeyCredentialRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new PasskeyCredentialRecord(
                reader.GetFieldValue<byte[]>(0), reader.GetInt64(1),
                reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3),
                checked((uint)reader.GetInt64(4)), reader.GetString(5),
                reader.GetDateTime(6), reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
        }
        return items;
    }

    private static void ValidateCredential(
        long actorId,
        long userId,
        byte[] credentialId,
        byte[] userHandle,
        byte[] publicKey,
        string? displayName,
        out string normalizedDisplayName)
    {
        if (actorId <= 0 || userId <= 0 || credentialId.Length is 0 or > 1024
            || userHandle.Length is 0 or > 128 || publicKey.Length is 0 or > 4096)
            throw new ArgumentException("Passkey credential material is out of bounds");
        normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? "Passkey" : displayName.Trim();
        if (normalizedDisplayName.Length > 200 || normalizedDisplayName.Any(char.IsControl))
            throw new ArgumentException("Passkey display name is invalid", nameof(displayName));
    }
}
