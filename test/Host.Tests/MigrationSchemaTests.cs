using Npgsql;
using Xunit;

namespace Sub2Api.Host.Tests;

public sealed class MigrationSchemaTests
{
    [Fact]
    public async Task TargetSchemaContainsPlatformProjectionAndMigrationColumns()
    {
        var connectionString = Environment.GetEnvironmentVariable("CDC_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["user_accounts"] = ["id", "email", "password_hash", "display_name", "status", "role", "oauth_provider", "oauth_id", "totp_secret", "totp_enabled", "totp_backup_codes", "created_at", "last_login_at"],
            ["user_api_keys"] = ["id", "api_key_id", "user_email", "key_hash", "key_prefix", "name", "status", "created_at", "last_used_at"],
            ["announcements"] = ["id", "title", "content", "status", "priority", "created_at", "expires_at"],
            ["audit_logs"] = ["id", "user_id", "action", "resource_type", "resource_id", "details", "ip_address", "created_at"],
            ["ops_metrics"] = ["id", "metric_name", "metric_value", "labels", "collected_at"],
            ["redeem_codes"] = ["id", "code", "discount_amount", "bonus_amount", "max_uses", "used_count", "status", "expires_at", "created_at", "last_redeemed_by"],
            ["redeem_code_redemptions"] = ["id", "code_id", "user_id", "bonus_amount", "created_at"],
            ["payment_orders"] = ["id", "idempotency_key", "user_id", "amount", "currency", "provider", "provider_order_id", "status", "description", "created_at", "paid_at"],
            ["balance_ledger"] = ["id", "user_id", "payment_id", "reference", "amount", "created_at"],
            ["subscription_plans"] = ["id", "name", "price_monthly", "quota_usd", "features", "status"],
            ["user_subscriptions"] = ["id", "user_id", "plan_id", "status", "started_at", "expires_at"],
            ["channel_monitors"] = ["id", "account_id", "status", "latency_ms", "last_error", "checked_at"],
            ["usage_summary_daily"] = ["id", "user_id", "model", "date", "request_count", "input_tokens", "output_tokens", "total_cost_usd"],
            ["referral_codes"] = ["id", "user_id", "code", "total_referrals", "total_bonus_usd", "created_at"],
            ["referral_records"] = ["id", "referrer_user_id", "referred_user_id", "bonus_usd", "created_at"],
            ["content_audit_rules"] = ["id", "pattern", "action_type", "scope", "status", "created_at"],
            ["content_audit_logs"] = ["id", "user_id", "request_id", "matched_rule", "action", "content_snippet", "created_at"],
            ["proxies"] = ["id", "name", "type", "host", "port", "username", "password", "status", "latency_ms", "created_at"],
            ["tls_fingerprint_profiles"] = ["id", "name", "ja3_hash", "ja4_hash", "cipher_suites", "status", "created_at"],
            ["usage_logs"] = ["request_id", "lease_token", "api_key_id", "user_id", "account_id", "group_id", "model", "upstream_model", "input_tokens", "output_tokens", "cache_create_tokens", "cache_read_tokens", "cost_usd", "duration_ms", "first_token_ms", "stream", "client_disconnect", "created_at"],
            ["request_leases"] = ["lease_token", "request_id", "api_key_hash", "api_key_id", "user_id", "account_id", "group_id", "model", "upstream_model", "inbound_endpoint", "rate_multiplier", "hold_handle", "hold_amount", "status", "final_cost_usd", "abort_reason", "created_at", "expires_at", "finalized_at"],
            ["usage_events"] = ["lease_token", "request_id", "api_key_id", "user_id", "account_id", "group_id", "model", "upstream_model", "inbound_endpoint", "input_tokens", "output_tokens", "cache_create_tokens", "cache_read_tokens", "cost_usd", "duration_ms", "first_token_ms", "status_code", "stream", "client_disconnect", "created_at"],
            ["usage_outbox"] = ["id", "lease_token", "event_type", "attempts", "next_attempt_at", "processed_at", "last_error", "created_at", "claimed_by", "claimed_until", "dead_lettered_at"],
            ["accounts"] = ["id", "name", "platform", "type", "base_url", "credentials", "proxy_url", "tls_fingerprint", "model_mapping", "supported_models", "concurrency", "priority", "load_factor", "rate_multiplier", "schedulable", "status", "error_message", "last_used_at", "created_at", "updated_at", "deleted_at"],
            ["groups"] = ["id", "name", "platform", "description", "rate_multiplier", "is_exclusive", "status", "daily_limit_usd", "claude_code_only", "fallback_group_id", "model_routing_enabled", "model_routing", "member_account_ids", "rpm_limit", "peak_multiplier", "peak_start_hour", "peak_end_hour", "created_at", "updated_at", "deleted_at"],
            ["account_groups"] = ["account_id", "group_id", "priority", "created_at"],
            ["balance_holds"] = ["hold_id", "user_id", "lease_token", "amount", "status", "created_at", "finalized_at"],
            ["settlement_effects"] = ["lease_token", "effect_type", "applied_at"],
            ["migration_fence"] = ["id", "epoch", "write_primary", "mode", "reason", "updated_by", "updated_at"],
            ["cdc_inbox"] = ["event_id", "epoch", "source_lsn", "transaction_id", "aggregate_type", "aggregate_id", "operation", "schema_version", "payload_hash", "envelope", "status", "attempts", "last_error", "received_at", "applied_at", "next_attempt_at"],
            ["cdc_checkpoints"] = ["connector_name", "source_lsn", "source_lsn_value", "snapshot_completed", "last_event_id", "updated_at", "last_partition", "last_offset"],
            ["cdc_dead_letters"] = ["event_id", "envelope", "reason", "attempts", "created_at", "replayed_at"],
            ["cdc_sync_acks"] = ["event_id", "epoch", "aggregate_type", "aggregate_id", "status", "error_code", "acked_at"],
            ["cdc_credential_payloads"] = ["event_id", "epoch", "aggregate_type", "aggregate_id", "key_version", "ciphertext", "payload_hash", "applied_at", "created_at", "source_lsn", "transaction_id", "operation", "occurred_at"],
            ["cdc_rejected_messages"] = ["id", "connector_name", "topic", "partition_id", "offset_value", "message_sha256", "message_bytes", "reason", "received_at"],
            ["migration_fence_events"] = ["id", "from_epoch", "to_epoch", "from_primary", "from_mode", "to_primary", "to_mode", "reason", "updated_by", "transitioned_at"]
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
    }
}
