using Npgsql;

namespace ScalaAPI.Admin.Data;

public sealed record UserAnnouncementView(
    long Id,
    string Title,
    string Content,
    int Priority,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? ReadAt);

public sealed record AnnouncementReadResult(bool Created, DateTime ReadAt);

public sealed class AnnouncementStore(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<UserAnnouncementView>> ListForUserAsync(
        long userId,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        limit = Math.Clamp(limit, 1, 100);
        await using var command = dataSource.CreateCommand("""
            SELECT a.id, a.title, a.content, a.priority, a.created_at, a.expires_at,
                   r.read_at
            FROM announcements a
            LEFT JOIN announcement_reads r
              ON r.announcement_id = a.id AND r.user_id = $1
            WHERE a.status = 'published'
              AND (a.expires_at IS NULL OR a.expires_at > now())
            ORDER BY a.priority DESC, a.created_at DESC, a.id DESC
            LIMIT $2
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(limit);
        var items = new List<UserAnnouncementView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new UserAnnouncementView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }
        return items;
    }

    public async Task<AnnouncementReadResult?> MarkReadAsync(
        long userId,
        long announcementId,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (userId <= 0 || announcementId <= 0)
            throw new ArgumentOutOfRangeException();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = """
                SELECT 1 FROM announcements
                WHERE id = $1 AND status = 'published'
                  AND (expires_at IS NULL OR expires_at > now())
                """;
            exists.Parameters.AddWithValue(announcementId);
            if (await exists.ExecuteScalarAsync(ct) is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
        }

        DateTime readAt;
        var created = false;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO announcement_reads(user_id, announcement_id)
                VALUES ($1, $2)
                ON CONFLICT (user_id, announcement_id) DO NOTHING
                RETURNING read_at
                """;
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(announcementId);
            var value = await insert.ExecuteScalarAsync(ct);
            if (value is DateTime createdAt)
            {
                readAt = createdAt;
                created = true;
            }
            else
            {
                await using var existing = connection.CreateCommand();
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT read_at FROM announcement_reads
                    WHERE user_id = $1 AND announcement_id = $2
                    """;
                existing.Parameters.AddWithValue(userId);
                existing.Parameters.AddWithValue(announcementId);
                readAt = (DateTime)(await existing.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException("Announcement read state disappeared"));
            }
        }

        if (created)
        {
            await using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit_logs(
                    user_id, action, resource_type, resource_id, details, ip_address)
                VALUES ($1, 'announcement.read', 'announcement', $2, '{}', $3)
                """;
            audit.Parameters.AddWithValue(userId);
            audit.Parameters.AddWithValue(announcementId.ToString());
            audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(created, readAt);
    }
}
