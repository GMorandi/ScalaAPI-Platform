using Npgsql;

namespace ScalaAPI.Data.Voice;

public sealed record AudioHistoryEntry(
    long Id,
    long UserId,
    long ApiKeyId,
    string LeaseId,
    string AudioType,
    string Model,
    string Voice,
    int InputLength,
    decimal OutputDurationSec,
    string ResponseFormat,
    string Language,
    int ResultCount,
    string ProviderPlatform,
    long ProviderAccountId,
    string Status,
    string? ErrorCode,
    DateTimeOffset CreatedAt);

public interface IAudioHistoryStore
{
    Task<AudioHistoryEntry> RecordAsync(long userId, long apiKeyId, string leaseId,
        string audioType, string model, string voice, int inputLength,
        decimal outputDurationSec, string responseFormat, string language,
        int resultCount, string providerPlatform, long providerAccountId,
        string status, string? errorCode, CancellationToken ct = default);

    Task<IReadOnlyList<AudioHistoryEntry>> ListByUserAsync(long userId,
        DateTimeOffset? since, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<AudioHistoryEntry>> ListForAuditAsync(string? providerPlatform,
        string? status, int limit, CancellationToken ct = default);
}

public sealed class AudioHistoryStore : IAudioHistoryStore
{
    private readonly NpgsqlDataSource _dataSource;

    public AudioHistoryStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AudioHistoryEntry> RecordAsync(long userId, long apiKeyId, string leaseId,
        string audioType, string model, string voice, int inputLength,
        decimal outputDurationSec, string responseFormat, string language,
        int resultCount, string providerPlatform, long providerAccountId,
        string status, string? errorCode, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("""
            INSERT INTO audio_history (user_id, api_key_id, lease_id, audio_type, model, voice,
                input_length, output_duration_sec, response_format, language,
                result_count, provider_platform, provider_account_id, status, error_code)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            ON CONFLICT (lease_id) DO UPDATE SET
                status = EXCLUDED.status,
                error_code = EXCLUDED.error_code,
                result_count = EXCLUDED.result_count,
                output_duration_sec = EXCLUDED.output_duration_sec
            RETURNING id, user_id, api_key_id, lease_id, audio_type, model, voice,
                input_length, output_duration_sec, response_format, language,
                result_count, provider_platform, provider_account_id, status, error_code, created_at
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(apiKeyId);
        cmd.Parameters.AddWithValue(leaseId);
        cmd.Parameters.AddWithValue(audioType);
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.AddWithValue(voice);
        cmd.Parameters.AddWithValue(inputLength);
        cmd.Parameters.AddWithValue(outputDurationSec);
        cmd.Parameters.AddWithValue(responseFormat);
        cmd.Parameters.AddWithValue(language);
        cmd.Parameters.AddWithValue(resultCount);
        cmd.Parameters.AddWithValue(providerPlatform);
        cmd.Parameters.AddWithValue(providerAccountId);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(errorCode is null ? DBNull.Value : (object)errorCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Audio history insert returned no rows");
        return ReadEntry(reader);
    }

    public async Task<IReadOnlyList<AudioHistoryEntry>> ListByUserAsync(long userId,
        DateTimeOffset? since, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var cmd = _dataSource.CreateCommand("""
            SELECT id, user_id, api_key_id, lease_id, audio_type, model, voice,
                input_length, output_duration_sec, response_format, language,
                result_count, provider_platform, provider_account_id, status, error_code, created_at
            FROM audio_history
            WHERE user_id = $1 AND ($2::timestamptz IS NULL OR created_at > $2)
            ORDER BY created_at DESC
            LIMIT $3
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(since.HasValue ? (object)since.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(limit);
        var results = new List<AudioHistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadEntry(reader));
        return results;
    }

    public async Task<IReadOnlyList<AudioHistoryEntry>> ListForAuditAsync(string? providerPlatform,
        string? status, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var cmd = _dataSource.CreateCommand("""
            SELECT id, user_id, api_key_id, lease_id, audio_type, model, voice,
                input_length, output_duration_sec, response_format, language,
                result_count, provider_platform, provider_account_id, status, error_code, created_at
            FROM audio_history
            WHERE ($1::text IS NULL OR provider_platform = $1)
              AND ($2::text IS NULL OR status = $2)
            ORDER BY created_at DESC
            LIMIT $3
            """);
        cmd.Parameters.AddWithValue(providerPlatform is null ? DBNull.Value : (object)providerPlatform);
        cmd.Parameters.AddWithValue(status is null ? DBNull.Value : (object)status);
        cmd.Parameters.AddWithValue(limit);
        var results = new List<AudioHistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadEntry(reader));
        return results;
    }

    private static AudioHistoryEntry ReadEntry(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetInt32(7),
        reader.GetDecimal(8),
        reader.GetString(9),
        reader.GetString(10),
        reader.GetInt32(11),
        reader.GetString(12),
        reader.GetInt64(13),
        reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.GetFieldValue<DateTimeOffset>(16));
}
