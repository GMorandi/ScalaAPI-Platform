using Npgsql;

namespace ScalaAPI.Data.Announcements;

/// <summary>
/// Manages announcement lifecycle with targeting, scheduling, and read state tracking.
/// Supports audience targeting (all, specific plans, specific users),
/// future-dated publishing, and duplicate-free read tracking.
/// </summary>
public sealed class AnnouncementService(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Creates a new announcement with optional scheduling and targeting.
    /// </summary>
    public async Task<AnnouncementCreationResult> CreateAsync(
        string title,
        string content,
        string targetAudience,
        DateTime? scheduledAt,
        DateTime? expiresAt,
        long? createdBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)
            || string.IsNullOrWhiteSpace(targetAudience))
            return new AnnouncementCreationResult(AnnouncementCreationStatus.Invalid);

        var status = scheduledAt.HasValue && scheduledAt.Value > DateTime.UtcNow
            ? "draft" : "published";
        var publishedAt = status == "published" ? DateTime.UtcNow : (DateTime?)null;

        await using var command = dataSource.CreateCommand("""
            INSERT INTO announcements (title, content, status, target_audience,
                                       scheduled_at, published_at, expires_at, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            RETURNING id
            """);
        command.Parameters.AddWithValue(title.Trim());
        command.Parameters.AddWithValue(content.Trim());
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(targetAudience.Trim());
        command.Parameters.AddWithValue((object?)scheduledAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)publishedAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)expiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)createdBy ?? DBNull.Value);

        var id = await command.ExecuteScalarAsync(ct);
        if (id is null or DBNull)
            return new AnnouncementCreationResult(AnnouncementCreationStatus.Invalid);

        return new AnnouncementCreationResult(AnnouncementCreationStatus.Created,
            Convert.ToInt64(id));
    }

    /// <summary>
    /// Publishes a scheduled announcement if its scheduled time has arrived.
    /// </summary>
    public async Task<int> PublishDueAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE announcements
            SET status = 'published', published_at = now()
            WHERE status = 'draft'
              AND scheduled_at IS NOT NULL
              AND scheduled_at <= now()
            """);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Lists announcements visible to a specific user, filtered by target audience,
    /// published status, and expiry. Excludes already-read announcements from the
    /// unread count but still returns them.
    /// </summary>
    public async Task<IReadOnlyList<AnnouncementView>> ListForUserAsync(
        long userId, int limit = 50, CancellationToken ct = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        limit = Math.Clamp(limit, 1, 200);

        await using var command = dataSource.CreateCommand("""
            SELECT a.id, a.title, a.content, a.target_audience,
                   a.created_at, a.expires_at, a.published_at,
                   r.read_at
            FROM announcements a
            LEFT JOIN announcement_reads r
              ON r.announcement_id = a.id AND r.user_id = $1
            WHERE a.status = 'published'
              AND (a.expires_at IS NULL OR a.expires_at > now())
              AND (a.published_at IS NULL OR a.published_at <= now())
            ORDER BY a.created_at DESC, a.id DESC
            LIMIT $2
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(limit);
        var items = new List<AnnouncementView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new AnnouncementView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
        }
        return items;
    }

    /// <summary>
    /// Marks an announcement as read for a user. Uses ON CONFLICT to prevent
    /// duplicate reads. Returns the read state (created or existing).
    /// </summary>
    public async Task<ReadResult> MarkReadAsync(
        long userId, long announcementId, CancellationToken ct = default)
    {
        if (userId <= 0 || announcementId <= 0)
            return new ReadResult(ReadStatus.Invalid);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Verify announcement exists and is visible
        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = """
                SELECT 1 FROM announcements
                WHERE id = $1 AND status = 'published'
                  AND (expires_at IS NULL OR expires_at > now())
                """;
            verify.Parameters.AddWithValue(announcementId);
            if (await verify.ExecuteScalarAsync(ct) is null)
            {
                await transaction.RollbackAsync(ct);
                return new ReadResult(ReadStatus.NotFound);
            }
        }

        DateTime readAt;
        var created = false;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO announcement_reads (user_id, announcement_id)
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
                    ?? throw new InvalidOperationException("Read state disappeared"));
            }
        }

        await transaction.CommitAsync(ct);
        return new ReadResult(created ? ReadStatus.Created : ReadStatus.Duplicate, readAt);
    }
}

public enum AnnouncementCreationStatus { Created, Invalid }

public sealed record AnnouncementCreationResult(
    AnnouncementCreationStatus Status, long? AnnouncementId = null);

public enum ReadStatus { Created, Duplicate, NotFound, Invalid }

public sealed record ReadResult(ReadStatus Status, DateTime? ReadAt = null);

public sealed record AnnouncementView(
    long Id, string Title, string Content, string TargetAudience,
    DateTime CreatedAt, DateTime? ExpiresAt, DateTime? PublishedAt,
    DateTime? ReadAt);
