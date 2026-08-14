using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class AnnouncementStoreTests
{
    [Fact]
    public async Task PublishedAnnouncementsExposeReadStateAndReadIsIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var userId = 9_980_000L + Random.Shared.Next(1, 40_000);
        var announcementId = 9_980_000L + Random.Shared.Next(1, 40_000);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new AnnouncementStore(dataSource);
        try
        {
            await ExecuteAsync(dataSource, """
                INSERT INTO user_accounts(id, email, status, role)
                VALUES ($1, $2, 'active', 'user')
                """, userId, $"announcement-{userId}@example.test");
            await ExecuteAsync(dataSource, """
                INSERT INTO announcements(id, title, content, status, priority)
                VALUES ($1, 'Release note', 'New greenfield capability', 'published', 10)
                """, announcementId);

            var unread = await store.ListForUserAsync(userId);
            var item = Assert.Single(unread);
            Assert.Equal(announcementId, item.Id);
            Assert.Null(item.ReadAt);

            var first = await store.MarkReadAsync(userId, announcementId, "127.0.0.1");
            Assert.NotNull(first);
            Assert.True(first!.Created);
            var second = await store.MarkReadAsync(userId, announcementId, "127.0.0.1");
            Assert.NotNull(second);
            Assert.False(second!.Created);
            Assert.Equal(first.ReadAt, second.ReadAt);

            var read = Assert.Single(await store.ListForUserAsync(userId));
            Assert.Equal(first.ReadAt, read.ReadAt);
            Assert.Equal(1, await ScalarAsync(dataSource,
                "SELECT count(*) FROM audit_logs WHERE user_id = $1 AND action = 'announcement.read'", userId));
        }
        finally
        {
            await ExecuteAsync(dataSource, "DELETE FROM announcement_reads WHERE user_id = $1", userId);
            await ExecuteAsync(dataSource, "DELETE FROM announcements WHERE id = $1", announcementId);
            await ExecuteAsync(dataSource, "DELETE FROM audit_logs WHERE user_id = $1 AND action = 'announcement.read'", userId);
            await ExecuteAsync(dataSource, "DELETE FROM user_accounts WHERE id = $1", userId);
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
