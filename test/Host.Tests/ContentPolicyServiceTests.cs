using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ContentPolicyServiceTests
{
    [Fact]
    public async Task BlockRuleIsEvaluatedBeforeDispatchAndAudited()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var userId = 9_500_000L + Random.Shared.Next(1, 100_000);
        var requestId = $"content-policy:{suffix}";
        var blockPattern = $"blocked-{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var command = dataSource.CreateCommand("""
                INSERT INTO content_audit_rules(pattern, action_type, scope, status)
                VALUES ($1, 'block', 'chat_completions', 'active')
                """))
            {
                command.Parameters.AddWithValue(blockPattern);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ContentPolicyService(
                dataSource, NullLogger<ContentPolicyService>.Instance);
            var decision = await service.EvaluateAsync(userId, requestId,
                "chat_completions", "chat_completions",
                $"hello {blockPattern} world");

            Assert.False(decision.Allowed);
            Assert.Equal("content_policy_blocked", decision.Code);
            var match = Assert.Single(decision.Matches);
            Assert.Equal("block", match.Action);

            await using var verify = dataSource.CreateCommand("""
                SELECT action, matched_rule, content_snippet
                FROM content_audit_logs
                WHERE request_id = $1
                """);
            verify.Parameters.AddWithValue(requestId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("block", reader.GetString(0));
            Assert.Equal(blockPattern, reader.GetString(1));
            Assert.Contains(blockPattern, reader.GetString(2));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand("""
                DELETE FROM content_audit_logs WHERE request_id = $1;
                DELETE FROM content_audit_rules WHERE pattern = $2;
                """);
            cleanup.Parameters.AddWithValue(requestId);
            cleanup.Parameters.AddWithValue(blockPattern);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ScopeMismatchDoesNotCreateAnAuditOrBlock()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var requestId = $"content-policy-scope:{suffix}";
        var pattern = $"scoped-{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var command = dataSource.CreateCommand("""
                INSERT INTO content_audit_rules(pattern, action_type, scope, status)
                VALUES ($1, 'block', 'messages', 'active')
                """))
            {
                command.Parameters.AddWithValue(pattern);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ContentPolicyService(
                dataSource, NullLogger<ContentPolicyService>.Instance);
            var decision = await service.EvaluateAsync(9_500_001L, requestId,
                "chat_completions", "chat_completions", pattern);

            Assert.True(decision.Allowed);
            Assert.Empty(decision.Matches);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM content_audit_rules WHERE pattern = $1");
            cleanup.Parameters.AddWithValue(pattern);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task OversizedBodyFailsClosedWithoutAProviderLease()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new ContentPolicyService(
            dataSource, NullLogger<ContentPolicyService>.Instance);
        var decision = await service.EvaluateAsync(1, "oversized", "messages", "messages",
            new string('x', ContentPolicyService.MaxBodyBytes + 1));

        Assert.False(decision.Allowed);
        Assert.Equal("content_policy_payload_too_large", decision.Code);
        Assert.Empty(decision.Matches);
    }
}
