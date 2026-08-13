using Npgsql;

namespace ScalaAPI.Data.Voice;

public sealed record VoiceEntry(
    long Id,
    long UserId,
    string Name,
    string Description,
    string VoiceType,
    string AudioUrl,
    string MetadataJson,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IVoiceStore
{
    Task<VoiceEntry> CreateAsync(long userId, string name, string description,
        string voiceType, string audioUrl, string metadataJson,
        CancellationToken ct = default);

    Task<VoiceEntry?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<VoiceEntry?> GetByUserAsync(long id, long userId, CancellationToken ct = default);

    Task<IReadOnlyList<VoiceEntry>> ListByUserAsync(long userId, string? status,
        int limit, CancellationToken ct = default);

    Task<VoiceEntry?> UpdateStatusAsync(long id, long userId, string status,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(long id, long userId, CancellationToken ct = default);
}

public sealed class VoiceStore : IVoiceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public VoiceStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<VoiceEntry> CreateAsync(long userId, string name, string description,
        string voiceType, string audioUrl, string metadataJson,
        CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("""
            INSERT INTO voices (user_id, name, description, voice_type, audio_url, metadata_json, status)
            VALUES ($1, $2, $3, $4, $5, $6, 'active')
            ON CONFLICT (user_id, name) DO UPDATE SET
                description = EXCLUDED.description,
                voice_type = EXCLUDED.voice_type,
                audio_url = EXCLUDED.audio_url,
                metadata_json = EXCLUDED.metadata_json,
                status = 'active',
                updated_at = now()
            RETURNING id, user_id, name, description, voice_type, audio_url, metadata_json, status, created_at, updated_at
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(description);
        cmd.Parameters.AddWithValue(voiceType);
        cmd.Parameters.AddWithValue(audioUrl);
        cmd.Parameters.AddWithValue(metadataJson);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Voice insert returned no rows");
        return ReadEntry(reader);
    }

    public async Task<VoiceEntry?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("""
            SELECT id, user_id, name, description, voice_type, audio_url, metadata_json, status, created_at, updated_at
            FROM voices WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    public async Task<VoiceEntry?> GetByUserAsync(long id, long userId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("""
            SELECT id, user_id, name, description, voice_type, audio_url, metadata_json, status, created_at, updated_at
            FROM voices WHERE id = $1 AND user_id = $2
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    public async Task<IReadOnlyList<VoiceEntry>> ListByUserAsync(long userId, string? status,
        int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var cmd = _dataSource.CreateCommand("""
            SELECT id, user_id, name, description, voice_type, audio_url, metadata_json, status, created_at, updated_at
            FROM voices WHERE user_id = $1 AND ($2::text IS NULL OR status = $2)
            ORDER BY created_at DESC
            LIMIT $3
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(status is null ? DBNull.Value : (object)status);
        cmd.Parameters.AddWithValue(limit);
        var results = new List<VoiceEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadEntry(reader));
        return results;
    }

    public async Task<VoiceEntry?> UpdateStatusAsync(long id, long userId, string status,
        CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("""
            UPDATE voices SET status = $3, updated_at = now()
            WHERE id = $1 AND user_id = $2
            RETURNING id, user_id, name, description, voice_type, audio_url, metadata_json, status, created_at, updated_at
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(status);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    public async Task<bool> DeleteAsync(long id, long userId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            "DELETE FROM voices WHERE id = $1 AND user_id = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(userId);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    private static VoiceEntry ReadEntry(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetFieldValue<DateTimeOffset>(9));
}
