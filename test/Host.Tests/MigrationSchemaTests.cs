using Npgsql;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class MigrationSchemaTests
{
    [Fact]
    public async Task GreenfieldSchemaContainsProductTablesAndNoRetiredControlTables()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["user_accounts"] = ["id", "email", "status", "role", "email_verified", "email_verified_at"],
            ["user_api_keys"] = ["id", "key_hash", "status", "scopes", "expires_at_ms"],
            ["api_key_audit_events"] = ["id", "api_key_id", "user_id", "actor_user_id",
                "action", "scopes", "expires_at_ms", "capability", "reason", "request_id", "created_at"],
            ["accounts"] = ["id", "name", "platform", "credentials", "status"],
            ["groups"] = ["id", "name", "platform", "status"],
            ["request_leases"] = ["lease_token", "request_id", "hold_amount", "status",
                "pricing_version", "price_input_per_million", "price_output_per_million",
                "reconciliation_needed_at", "forwarded_at", "output_started_at",
                "provider_status_code", "subscription_id", "subscription_hold_amount"],
            ["request_lease_events"] = ["lease_token", "event_type", "source", "detail",
                "provider_status_code", "created_at"],
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
                "object_key", "object_etag", "object_size", "object_status", "object_error",
                "object_verified_at", "object_reconcile_attempts", "object_next_check_at",
                "retention_until"],
            ["media_operation_items"] = ["item_id", "operation_id", "item_index", "custom_id",
                "provider_url", "object_key", "object_etag", "object_size", "content_type",
                "object_status", "output_url", "error", "retention_until", "object_verified_at",
                "object_reconcile_attempts", "object_next_check_at", "created_at", "updated_at"],
            ["content_audit_rules"] = ["id", "pattern", "action_type", "scope", "status", "stage",
                "evaluator_version", "classifier", "redact_content", "created_at"],
            ["content_audit_logs"] = ["id", "user_id", "request_id", "matched_rule", "action",
                "rule_id", "stage", "content_snippet", "evaluator_version", "classifier",
                "content_redacted", "policy_revision", "created_at"],
            ["content_policy_state"] = ["id", "revision", "evaluator_version", "updated_at"],
            ["content_policy_change_events"] = ["id", "revision", "action", "rule_id",
                "actor_id", "details", "created_at", "propagated_at", "claimed_by",
                "claimed_until", "attempts", "last_error"],
            ["content_policy_alert_events"] = ["id", "event_key", "kind", "severity",
                "rule_id", "user_id", "request_id", "stage", "code", "policy_revision",
                "details", "created_at"],
            ["content_classifier_metric_snapshots"] = ["instance_id", "sequence",
                "classifier", "requests", "matches", "no_matches", "unavailable",
                "protocol_errors", "cancellations", "duration_ticks", "bucket_0",
                "bucket_9", "captured_at"],
            ["content_classifier_budget_alerts"] = ["event_key", "classifier",
                "budget_kind", "status", "observed_value", "threshold_value",
                "sample_count", "first_seen_at", "last_seen_at", "resolved_at"],
            ["pricing_versions"] = ["version", "model", "effective_from", "source_provider",
                "source_model", "source_checksum"],
            ["ledger_reconciliation_runs"] = ["id", "status", "mismatch_total",
                "checked_accounts", "repaired_holds", "repaired_projections",
                "open_incidents", "resolved_incidents"],
            ["accounting_reconciliation_incidents"] = ["id", "incident_key", "kind",
                "severity", "user_id", "lease_token", "status", "expected", "actual",
                "occurrences", "last_run_id"],
            ["accounting_reconciliation_resolutions"] = ["id", "incident_id",
                "lease_token", "action", "evidence_type", "evidence", "reason",
                "actor_user_id", "idempotency_key", "request_fingerprint", "usage_payload"],
            ["entity_registry"] = ["entity_type", "entity_key", "entity_id", "status"],
            ["auth_sessions"] = ["session_id", "user_id", "refresh_token_hash", "expires_at", "revoked_at"],
            ["password_reset_tokens"] = ["token_hash", "user_id", "expires_at", "used_at"],
            ["email_verification_tokens"] = ["token_hash", "user_id", "expires_at", "used_at"],
            ["auth_totp_state"] = ["user_id", "failed_attempts", "window_started_at",
                "locked_until", "last_accepted_step", "updated_at"],
            ["auth_oauth_states"] = ["state_hash", "provider", "redirect_uri", "verifier_hash",
                "expires_at", "consumed_at", "created_at"],
            ["auth_abuse_counters"] = ["counter_key", "failure_count", "window_started_at",
                "locked_until", "updated_at"],
            ["passkey_challenges"] = ["challenge_id", "user_id", "flow", "options",
                "expires_at", "consumed_at", "created_at"],
            ["passkey_credentials"] = ["credential_id", "user_id", "user_handle", "public_key",
                "signature_counter", "display_name", "created_at", "last_used_at"],
            ["maintenance_operations"] = ["operation_key", "actor_user_id", "request_fingerprint",
                "dry_run", "result", "created_at", "completed_at"],
            ["backup_jobs"] = ["id", "kind", "idempotency_key", "request_fingerprint", "status",
                "artifact_path", "size_bytes", "sha256", "retention_until", "created_by",
                "created_at", "completed_at", "error_code", "error_detail"],
            ["backup_restore_runs"] = ["id", "backup_id", "idempotency_key", "request_fingerprint",
                "status", "target_fingerprint", "created_by", "created_at", "completed_at",
                "error_code", "error_detail"],
            ["backup_retention_policies"] = ["policy_id", "keep_daily", "keep_weekly", "keep_monthly",
                "offsite_enabled", "offsite_url", "offsite_bucket", "offsite_region",
                "encryption_enabled", "signing_enabled", "encryption_key_id", "updated_at"],
            ["backup_signing_keys"] = ["key_id", "algorithm", "key_material", "status",
                "created_at", "retired_at"],
            ["backup_offsite_uploads"] = ["upload_id", "backup_id", "provider", "remote_url",
                "remote_checksum", "status", "size_bytes", "started_at", "completed_at",
                "error_message", "created_at"],
            ["backup_schedule_claims"] = ["claim_id", "schedule_key", "worker_id", "claimed_at",
                "expires_at", "last_run_at", "last_run_status"],
            ["backup_rpo_rto_records"] = ["record_id", "backup_id", "measured_at", "rpo_seconds",
                "rto_seconds", "backup_duration_seconds", "restore_duration_seconds",
                "verification_passed", "details"],
            ["announcement_reads"] = ["user_id", "announcement_id", "read_at"],
            ["email_delivery_outbox"] = ["message_key", "recipient", "kind",
                "token_ciphertext", "expires_at", "status", "attempts", "available_at",
                "claimed_until", "last_error", "created_at", "sent_at"],
            ["provider_credential_refresh_attempts"] = ["id", "attempt_id", "account_id",
                "source", "version_before", "version_after", "outcome", "error_code",
                "token_endpoint_host", "started_at", "completed_at", "duration_ms"],
            ["payment_webhook_events"] = ["provider", "event_id", "event_type", "payload_hash", "status", "attempts", "next_attempt_at"],
            ["payment_orders"] = ["id", "user_id", "amount", "currency", "provider",
                "provider_order_id", "provider_payment_id", "checkout_url", "status", "idempotency_key",
                "refunded_amount"],
            ["payment_refunds"] = ["id", "payment_order_id", "user_id", "provider",
                "provider_order_id", "provider_payment_id", "provider_refund_id",
                "idempotency_key", "request_fingerprint", "amount", "currency", "reason",
                "status", "provider_status", "error_code", "actor_user_id", "attempts",
                "last_attempt_at", "next_attempt_at", "claimed_by", "claimed_until",
                "created_at", "updated_at"],
            ["subscription_events"] = ["subscription_id", "user_id", "event_type", "idempotency_key", "payload"],
            ["user_subscriptions"] = ["user_id", "plan_id", "status", "idempotency_key", "renewal_at",
                "quota_granted_usd", "quota_used_usd", "quota_reserved_usd"],
            ["config_revisions"] = ["revision_id", "config_key", "config_value", "previous_revision_id",
                "actor_user_id", "actor_reason", "created_at", "applied_at", "rolled_back_at", "status"],
            ["config_node_observations"] = ["node_id", "last_seen_revision", "last_seen_at"],
            ["monitor_v2_watermarks"] = ["dimension", "watermark_event_id", "watermark_timestamp", "updated_at"],
            ["monitor_v2_rollups"] = ["rollup_id", "dimension", "dimension_value", "window_start",
                "window_end", "event_count", "error_count", "latency_p50", "latency_p95",
                "latency_p99", "unique_event_ids", "created_at"],
            ["monitor_v2_privacy_config"] = ["config_key", "redact_user_ids", "redact_prompts",
                "retention_days", "updated_at"]
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

    [Fact]
    public async Task ContentPolicyClassifierConstraintIncludesOpenAi()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT conname, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conname IN ('ck_content_audit_rules_classifier',
                              'ck_content_audit_logs_classifier')
            ORDER BY conname
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            definitions[reader.GetString(0)] = reader.GetString(1);

        Assert.Equal(2, definitions.Count);
        Assert.Contains("openai", definitions["ck_content_audit_rules_classifier"]);
        Assert.Contains("openai", definitions["ck_content_audit_logs_classifier"]);
    }
}
