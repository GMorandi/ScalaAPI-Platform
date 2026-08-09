using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class AuditLogStoreTests
{
    [Fact]
    public async Task AuditQueriesAreBoundedAndRedactSensitiveJsonFields()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var actorId = 9_500_000L + Random.Shared.Next(1, 400_000);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var insert = dataSource.CreateCommand("""
                INSERT INTO audit_logs(
                    user_id, action, resource_type, resource_id, details, ip_address)
                VALUES ($1, 'test.audit', 'test', 'redaction',
                        '{"token":"do-not-return","nested":{"password":"hidden","ok":true}}',
                        '127.0.0.1')
                """))
            {
                insert.Parameters.AddWithValue(actorId);
                await insert.ExecuteNonQueryAsync();
            }

            var store = new AuditLogStore(dataSource);
            var page = await store.ListAsync(actorId, "test.audit", null, null, 1, 500);
            Assert.Single(page.Items);
            Assert.Equal(1L, page.Total);
            Assert.Equal(100, page.Size);
            Assert.Contains("[redacted]", page.Items[0].Details);
            Assert.DoesNotContain("do-not-return", page.Items[0].Details);
            Assert.DoesNotContain("hidden", page.Items[0].Details);

            var export = await store.ListAsync(actorId, "test.audit", null, null, 1, 1_000,
                maximumSize: 1_000);
            Assert.Single(export.Items);
            Assert.Equal(1_000, export.Size);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM audit_logs WHERE user_id = $1 AND action = 'test.audit'");
            cleanup.Parameters.AddWithValue(actorId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
