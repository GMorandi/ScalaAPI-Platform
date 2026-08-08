using Npgsql;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class MigrationSchemaTests
{
    [Fact]
    public async Task GreenfieldSchemaContainsProductTablesAndNoRetiredControlTables()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["user_accounts"] = ["id", "email", "status", "role", "email_verified", "email_verified_at"],
            ["user_api_keys"] = ["id", "key_hash", "status"],
            ["accounts"] = ["id", "name", "platform", "credentials", "status"],
            ["groups"] = ["id", "name", "platform", "status"],
            ["request_leases"] = ["lease_token", "request_id", "hold_amount", "status",
                "pricing_version", "price_input_per_million", "price_output_per_million"],
            ["request_idempotency"] = ["api_key_id", "idempotency_key", "request_fingerprint", "lease_token", "status",
                "response_status_code", "response_content_type", "response_body", "completed_at"],
            ["usage_events"] = ["lease_token", "cost_usd", "input_tokens", "output_tokens"],
            ["usage_outbox"] = ["lease_token", "event_type", "processed_at"],
            ["balance_holds"] = ["hold_id", "amount", "status"],
            ["balance_ledger"] = ["user_id", "amount", "created_at", "lease_token", "entry_type",
                "idempotency_key", "description", "created_by", "ledger_version"],
            ["accounting_accounts"] = ["user_id", "posted_balance", "ledger_version"],
            ["accounting_projection_outbox"] = ["user_id", "posted_balance", "ledger_version",
                "attempts", "next_attempt_at", "claimed_until"],
            ["media_operations"] = ["operation_id", "idempotency_key", "status", "output_url",
                "object_key", "object_etag", "object_size", "object_status", "object_error"],
            ["pricing_versions"] = ["version", "model", "effective_from"],
            ["ledger_reconciliation_runs"] = ["id", "status", "mismatch_total"],
            ["entity_registry"] = ["entity_type", "entity_key", "entity_id", "status"],
            ["auth_sessions"] = ["session_id", "user_id", "refresh_token_hash", "expires_at", "revoked_at"],
            ["password_reset_tokens"] = ["token_hash", "user_id", "expires_at", "used_at"],
            ["email_verification_tokens"] = ["token_hash", "user_id", "expires_at", "used_at"],
            ["payment_webhook_events"] = ["provider", "event_id", "event_type", "payload_hash", "status", "attempts", "next_attempt_at"],
            ["subscription_events"] = ["subscription_id", "user_id", "event_type", "idempotency_key", "payload"],
            ["user_subscriptions"] = ["user_id", "plan_id", "status", "idempotency_key", "renewal_at", "quota_granted_usd", "quota_used_usd"]
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            actual.Add($"{reader.GetString(0)}.{reader.GetString(1)}");

        var missing = required.SelectMany(pair => pair.Value
            .Where(column => !actual.Contains($"{pair.Key}.{column}"))
            .Select(column => $"{pair.Key}.{column}")).ToArray();
        Assert.Empty(missing);
        await reader.DisposeAsync();

        var retired = new[]
        {
            "migration_fence", "migration_fence_events", "cdc_inbox", "cdc_checkpoints",
            "cdc_dead_letters", "cdc_sync_acks", "cdc_credential_payloads", "cdc_rejected_messages"
        };
        await using var retiredCommand = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public'
            """, connection);
        await using var retiredReader = await retiredCommand.ExecuteReaderAsync();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (await retiredReader.ReadAsync()) tables.Add(retiredReader.GetString(0));
        Assert.DoesNotContain(retired, tables.Contains);
        await retiredReader.DisposeAsync();

    }
}
