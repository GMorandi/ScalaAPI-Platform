using System.Data;
using System.Security.Cryptography;
using Npgsql;
using OtpNet;

namespace ScalaAPI.Admin.Auth;

public enum TotpVerificationStatus
{
    Accepted,
    Invalid,
    Replayed,
    Locked,
    NotConfigured,
}

public sealed record TotpVerificationResult(
    TotpVerificationStatus Status,
    int RetryAfterSeconds = 0,
    bool UsedBackupCode = false)
{
    public bool Accepted => Status == TotpVerificationStatus.Accepted;
}

/// <summary>
/// Performs TOTP verification and abuse-state changes under one PostgreSQL transaction.
/// The state is intentionally database-owned so multiple Admin API instances share the
/// same lockout and time-step replay policy.
/// </summary>
public sealed class TotpVerificationService
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const int MaxFailures = 5;

    private readonly NpgsqlDataSource dataSource;
    private readonly SecretProtector protector;
    private readonly TimeProvider timeProvider;

    public TotpVerificationService(
        NpgsqlDataSource dataSource,
        SecretProtector protector,
        TimeProvider? timeProvider = null)
    {
        this.dataSource = dataSource;
        this.protector = protector;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<TotpVerificationResult> VerifyAsync(
        long userId,
        string code,
        bool allowBackupCodes,
        CancellationToken ct = default) =>
        ExecuteAsync(userId, code, allowBackupCodes, enable: false, disable: false,
            backupCodeHashes: null, ct);

    public Task<TotpVerificationResult> EnableAsync(
        long userId,
        string code,
        IReadOnlyList<string> backupCodeHashes,
        CancellationToken ct = default) =>
        ExecuteAsync(userId, code, allowBackupCodes: false, enable: true, disable: false,
            backupCodeHashes, ct);

    public Task<TotpVerificationResult> DisableAsync(
        long userId,
        string code,
        CancellationToken ct = default) =>
        ExecuteAsync(userId, code, allowBackupCodes: false, enable: false, disable: true,
            backupCodeHashes: null, ct);

    private async Task<TotpVerificationResult> ExecuteAsync(
        long userId,
        string code,
        bool allowBackupCodes,
        bool enable,
        bool disable,
        IReadOnlyList<string>? backupCodeHashes,
        CancellationToken ct)
    {
        if (userId <= 0) return new(TotpVerificationStatus.NotConfigured);
        if (enable && (backupCodeHashes is null || backupCodeHashes.Count != 10))
            throw new ArgumentException("Exactly ten backup code hashes are required", nameof(backupCodeHashes));
        if (disable && allowBackupCodes)
            throw new ArgumentException("Disable verification cannot accept backup codes", nameof(allowBackupCodes));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var account = await ReadAccountAsync(connection, transaction, userId, ct);
            if (account is null || account.Status != "active" || string.IsNullOrWhiteSpace(account.Secret))
            {
                await transaction.CommitAsync(ct);
                return new(TotpVerificationStatus.NotConfigured);
            }

            await EnsureStateAsync(connection, transaction, userId, now, ct);
            var state = await ReadStateAsync(connection, transaction, userId, ct)
                ?? throw new InvalidOperationException("TOTP state disappeared while locked");
            if (state.LockedUntil is not null && state.LockedUntil > now)
            {
                await transaction.CommitAsync(ct);
                return new(TotpVerificationStatus.Locked,
                    Math.Max(1, (int)Math.Ceiling((state.LockedUntil.Value - now).TotalSeconds)));
            }

            var verification = VerifyCode(account, code, allowBackupCodes, protector, now);
            if (verification.Kind == CodeKind.Totp
                && state.LastAcceptedStep is not null
                && verification.Step <= state.LastAcceptedStep.Value)
            {
                verification = new(CodeKind.Replayed, verification.Step, -1);
            }

            if (verification.Kind is CodeKind.Invalid or CodeKind.Replayed)
            {
                var failureWindowStarted = state.WindowStartedAt;
                var failures = state.FailedAttempts;
                if (now - failureWindowStarted >= FailureWindow)
                {
                    failureWindowStarted = now;
                    failures = 0;
                }

                failures++;
                DateTime? lockedUntil = failures >= MaxFailures
                    ? now + LockoutDuration : null;
                await UpdateStateAsync(connection, transaction, userId, failures,
                    failureWindowStarted, lockedUntil, state.LastAcceptedStep, now, ct);
                await transaction.CommitAsync(ct);
                return lockedUntil is not null
                    ? new(TotpVerificationStatus.Locked,
                        (int)Math.Ceiling(LockoutDuration.TotalSeconds))
                    : new(verification.Kind == CodeKind.Replayed
                        ? TotpVerificationStatus.Replayed : TotpVerificationStatus.Invalid);
            }

            var remainingBackupCodes = account.BackupCodes;
            if (verification.Kind == CodeKind.Backup)
            {
                var codes = SplitBackupCodes(account.BackupCodes);
                codes.RemoveAt(verification.BackupIndex);
                remainingBackupCodes = string.Join(',', codes);
            }

            await UpdateStateAsync(connection, transaction, userId, 0, now, null,
                verification.Kind == CodeKind.Totp ? verification.Step : state.LastAcceptedStep,
                now, ct);

            if (enable)
            {
                await UpdateAccountAsync(connection, transaction, userId, true,
                    account.Secret, string.Join(',', backupCodeHashes!), ct);
            }
            else if (disable)
            {
                await UpdateAccountAsync(connection, transaction, userId, false,
                    null, null, ct);
            }
            else if (verification.Kind == CodeKind.Backup)
            {
                await UpdateAccountAsync(connection, transaction, userId, account.TotpEnabled,
                    account.Secret, remainingBackupCodes, ct);
            }

            await transaction.CommitAsync(ct);
            return new(TotpVerificationStatus.Accepted,
                UsedBackupCode: verification.Kind == CodeKind.Backup);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static CodeVerification VerifyCode(
        AccountState account,
        string? code,
        bool allowBackupCodes,
        SecretProtector protector,
        DateTime now)
    {
        var normalized = code?.Trim() ?? "";
        if (normalized.Length == 6 && normalized.All(static c => c is >= '0' and <= '9'))
        {
            try
            {
                var secret = Base32Encoding.ToBytes(protector.Unprotect(account.Secret!));
                var totp = new Totp(secret);
                if (totp.VerifyTotp(now, normalized, out long step, new VerificationWindow(0, 0)))
                    return new(CodeKind.Totp, step, -1);
            }
            catch (CryptographicException)
            {
                return new(CodeKind.Invalid, -1, -1);
            }
            catch (FormatException)
            {
                return new(CodeKind.Invalid, -1, -1);
            }
            catch (ArgumentException)
            {
                return new(CodeKind.Invalid, -1, -1);
            }
        }

        if (allowBackupCodes)
        {
            var codes = SplitBackupCodes(account.BackupCodes);
            for (var index = 0; index < codes.Count; index++)
            {
                if (BCrypt.Net.BCrypt.Verify(normalized, codes[index]))
                    return new(CodeKind.Backup, -1, index);
            }
        }

        return new(CodeKind.Invalid, -1, -1);
    }

    private static List<string> SplitBackupCodes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static async Task<AccountState?> ReadAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT status, totp_secret, totp_enabled, totp_backup_codes
            FROM user_accounts WHERE id = $1 FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task EnsureStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        DateTime now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO auth_totp_state(user_id, window_started_at, updated_at)
            VALUES ($1, $2, $2)
            ON CONFLICT (user_id) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<TotpState?> ReadStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT failed_attempts, window_started_at, locked_until, last_accepted_step
            FROM auth_totp_state WHERE user_id = $1 FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(
            reader.GetInt32(0), reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private static async Task UpdateStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        int failures, DateTime windowStarted, DateTime? lockedUntil, long? lastStep,
        DateTime now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE auth_totp_state
            SET failed_attempts = $2, window_started_at = $3, locked_until = $4,
                last_accepted_step = $5, updated_at = $6
            WHERE user_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(failures);
        command.Parameters.AddWithValue(windowStarted);
        command.Parameters.AddWithValue((object?)lockedUntil ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)lastStep ?? DBNull.Value);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        bool enabled, string? secret, string? backupCodes, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE user_accounts
            SET totp_enabled = $2, totp_secret = $3, totp_backup_codes = $4
            WHERE id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(enabled);
        command.Parameters.AddWithValue((object?)secret ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)backupCodes ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record AccountState(
        string Status, string? Secret, bool TotpEnabled, string? BackupCodes);

    private sealed record TotpState(
        int FailedAttempts, DateTime WindowStartedAt, DateTime? LockedUntil,
        long? LastAcceptedStep);

    private enum CodeKind
    {
        Invalid,
        Replayed,
        Totp,
        Backup,
    }

    private sealed record CodeVerification(CodeKind Kind, long Step, int BackupIndex);
}
