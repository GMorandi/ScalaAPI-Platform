using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;
using ScalaAPI.Data.Accounting;

namespace ScalaAPI.Host.Services;

public sealed record RequestLease(
    string LeaseToken,
    string RequestId,
    string ApiKeyHash,
    long ApiKeyId,
    long UserId,
    long AccountId,
    long GroupId,
    string Model,
    string UpstreamModel,
    string InboundEndpoint,
    decimal RateMultiplier,
    string? HoldHandle,
    decimal HoldAmount,
    string Status,
    decimal? FinalCostUsd,
    DateTime ExpiresAt,
    string? PricingVersion,
    decimal? PriceInputPerMillion,
    decimal? PriceOutputPerMillion,
    decimal? PriceCacheCreatePerMillion,
    decimal? PriceCacheReadPerMillion,
    decimal? PriceImageInputPerUnit,
    decimal? PriceImageOutputPerUnit,
    decimal? PriceVideoPerSecond,
    decimal? PriceRealtimePerMinute,
    decimal? PriceSearchPerQuery = null,
    decimal? PriceAudioPerMinute = null,
    decimal? PriceCharacterPerMillion = null,
    decimal? PriceLongContextPerMillion = null,
    string ObservedModel = "",
    string PriceSourceProvider = "",
    string PriceSourceChecksum = "",
    long? SubscriptionId = null,
    decimal SubscriptionHoldAmount = 0m)
{
    public ModelPrice? PriceSnapshot => PricingVersion is null
        || PriceInputPerMillion is null || PriceOutputPerMillion is null
        || PriceCacheCreatePerMillion is null || PriceCacheReadPerMillion is null
        || PriceImageInputPerUnit is null || PriceImageOutputPerUnit is null
        || PriceVideoPerSecond is null || PriceRealtimePerMinute is null
        ? null
        : new ModelPrice(PriceInputPerMillion.Value, PriceOutputPerMillion.Value,
            PriceCacheCreatePerMillion.Value, PriceCacheReadPerMillion.Value,
            PriceImageInputPerUnit.Value, PriceImageOutputPerUnit.Value,
            PriceVideoPerSecond.Value, PriceRealtimePerMinute.Value,
            PriceSearchPerQuery ?? 0m, PriceAudioPerMinute ?? 0m,
            PriceCharacterPerMillion ?? 0m, PriceLongContextPerMillion ?? 0m,
            PricingVersion);
}

public sealed record LeaseCreateRequest(
    string LeaseToken,
    string RequestId,
    string ApiKeyHash,
    long ApiKeyId,
    long UserId,
    long AccountId,
    long GroupId,
    string Model,
    string UpstreamModel,
    string InboundEndpoint,
    decimal RateMultiplier,
    string? HoldHandle,
    decimal HoldAmount,
    DateTime ExpiresAt,
    string IdempotencyKey = "",
    string RequestFingerprint = "",
    ModelPrice? Price = null);

public sealed record LeaseCreateResult(
    bool Created,
    bool Replay,
    bool Conflict,
    bool InsufficientFunds,
    bool SubscriptionQuotaExceeded = false)
{
    public static LeaseCreateResult New() => new(true, false, false, false);
    public static LeaseCreateResult Duplicate() => new(false, true, false, false);
    public static LeaseCreateResult IdempotencyConflict() => new(false, false, true, false);
    public static LeaseCreateResult NoFunds() => new(false, false, false, true);
    public static LeaseCreateResult QuotaExceeded() => new(false, false, false, false, true);
}

public sealed record IdempotencyLookup(
    bool Found,
    bool Conflict,
    int ResponseStatusCode,
    string ResponseContentType,
    string ResponseBody,
    string RequestId = "",
    string LeaseToken = "")
{
    public static IdempotencyLookup Missing() => new(false, false, 0, "", "");
    public static IdempotencyLookup Replay(int statusCode = 0, string contentType = "", string body = "") =>
        new(true, false, statusCode, contentType, body);
    public static IdempotencyLookup FingerprintConflict() => new(true, true, 0, "", "");
    public bool HasResponse => ResponseStatusCode > 0 && ResponseBody.Length > 0;
}

public sealed record LeaseCompletion(
    string LeaseToken,
    int InputTokens,
    int OutputTokens,
    int CacheCreateTokens,
    int CacheReadTokens,
    int DurationMs,
    int FirstTokenMs,
    int StatusCode,
    bool Stream,
    bool ClientDisconnect,
    int InputImageCount = 0,
    int OutputImageCount = 0,
    string ImageSize = "",
    int VideoCount = 0,
    string VideoResolution = "",
    int VideoDurationSeconds = 0,
    int RealtimeDurationMs = 0,
    int RealtimeFrames = 0,
    string DisconnectReason = "",
    string ProviderUsageJson = "",
    int ReasoningTokens = 0,
    string ServiceTier = "",
    string UpstreamEndpoint = "",
    string CancellationReason = "",
    string MediaOperationId = "",
    string PricingVersion = "",
    int ResponseStatusCode = 0,
    string ResponseContentType = "",
    string ResponseBody = "",
    string ObservedModel = "",
    int SearchQueryCount = 0,
    decimal AudioMinutes = 0m,
    int CharacterCount = 0,
    int LongContextTokenCount = 0);

public sealed record WriteAck(bool Accepted, bool Duplicate, bool Retryable, string ErrorCode)
{
    public static WriteAck Ok() => new(true, false, false, "");
    public static WriteAck DuplicateWrite() => new(true, true, false, "");
    public static WriteAck Error(string code, bool retryable = false) =>
        new(false, false, retryable, code);
}

public enum LeaseEvidenceStage
{
    Forwarded,
    OutputStarted,
}

public enum LeaseAbortDisposition
{
    NoCharge,
    Unknown,
}

public sealed record OutboxItem(long Id, string LeaseToken, string EventType, int Attempts);

public sealed record ClaimedOutboxItem(OutboxItem Item, RequestLease Lease);

public sealed class RequestLeaseStore(
    NpgsqlDataSource dataSource,
    AccountingStore accounting,
    ModelPricingService pricing,
    ILogger<RequestLeaseStore> logger,
    FaultInjection? faults = null)
{
    public async Task<bool> CreateAsync(LeaseCreateRequest request, CancellationToken ct = default)
        => (await CreateDetailedAsync(request, ct)).Created;

    public async Task<LeaseCreateResult> CreateDetailedAsync(LeaseCreateRequest request,
        CancellationToken ct = default)
    {
        if (request.Price is null && pricing.TryGetPrice(request.Model, out var currentPrice))
            request = request with { Price = currentPrice };

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO request_leases (
                lease_token, request_id, api_key_hash, api_key_id, user_id,
                account_id, group_id, model, upstream_model, inbound_endpoint,
                rate_multiplier, hold_handle, hold_amount, status, expires_at,
                pricing_version, price_input_per_million, price_output_per_million,
                price_cache_create_per_million, price_cache_read_per_million,
                price_image_input_per_unit, price_image_output_per_unit,
                price_video_per_second, price_realtime_per_minute,
                observed_model, price_source_provider, price_source_checksum,
                price_search_per_query, price_audio_per_minute,
                price_character_per_million, price_long_context_per_million)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, 'held', $14,
                    $15, $16, $17, $18, $19, $20, $21, $22, $23,
                    $24, $25, $26, $27, $28, $29, $30)
            ON CONFLICT (request_id) DO NOTHING
            RETURNING lease_token
            """;
        command.Parameters.AddWithValue(request.LeaseToken);
        command.Parameters.AddWithValue(request.RequestId);
        command.Parameters.AddWithValue(request.ApiKeyHash);
        command.Parameters.AddWithValue(request.ApiKeyId);
        command.Parameters.AddWithValue(request.UserId);
        command.Parameters.AddWithValue(request.AccountId);
        command.Parameters.AddWithValue(request.GroupId);
        command.Parameters.AddWithValue(request.Model);
        command.Parameters.AddWithValue(request.UpstreamModel);
        command.Parameters.AddWithValue(request.InboundEndpoint);
        command.Parameters.AddWithValue(request.RateMultiplier);
        command.Parameters.AddWithValue((object?)request.HoldHandle ?? DBNull.Value);
        command.Parameters.AddWithValue(request.HoldAmount);
        command.Parameters.AddWithValue(request.ExpiresAt);
        command.Parameters.AddWithValue((object?)request.Price?.Version ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.InputPerMillion ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.OutputPerMillion ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.CacheCreatePerMillion ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.CacheReadPerMillion ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.ImageInputPerUnit ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.ImageOutputPerUnit ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.VideoPerSecond ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.RealtimePerMinute ?? DBNull.Value);
        command.Parameters.AddWithValue(""); // observed_model (set at completion)
        command.Parameters.AddWithValue(""); // price_source_provider
        command.Parameters.AddWithValue(""); // price_source_checksum
        command.Parameters.AddWithValue((object?)request.Price?.SearchPerQuery ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.AudioPerMinute ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.CharacterPerMillion ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)request.Price?.LongContextPerMillion ?? DBNull.Value);
        var inserted = await command.ExecuteScalarAsync(ct);
        if (inserted is null || inserted is DBNull)
        {
            await transaction.CommitAsync(ct);
            return LeaseCreateResult.Duplicate();
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            await using var idempotency = connection.CreateCommand();
            idempotency.Transaction = transaction;
            idempotency.CommandText = """
                INSERT INTO request_idempotency (
                    api_key_id, idempotency_key, request_fingerprint,
                    request_id, lease_token, status)
                VALUES ($1, $2, $3, $4, $5, 'active')
                ON CONFLICT (api_key_id, idempotency_key) DO NOTHING
                RETURNING idempotency_key
                """;
            idempotency.Parameters.AddWithValue(request.ApiKeyId);
            idempotency.Parameters.AddWithValue(request.IdempotencyKey.Trim());
            idempotency.Parameters.AddWithValue(request.RequestFingerprint ?? "");
            idempotency.Parameters.AddWithValue(request.RequestId);
            idempotency.Parameters.AddWithValue(request.LeaseToken);
            var keyInserted = await idempotency.ExecuteScalarAsync(ct);
            if (keyInserted is null || keyInserted is DBNull)
            {
                await using var existing = connection.CreateCommand();
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT request_fingerprint, status
                    FROM request_idempotency
                    WHERE api_key_id = $1 AND idempotency_key = $2
                    FOR UPDATE
                    """;
                existing.Parameters.AddWithValue(request.ApiKeyId);
                existing.Parameters.AddWithValue(request.IdempotencyKey.Trim());
                await using var existingReader = await existing.ExecuteReaderAsync(ct);
                if (!await existingReader.ReadAsync(ct))
                {
                    await transaction.RollbackAsync(ct);
                    return LeaseCreateResult.Duplicate();
                }

                var existingFingerprint = existingReader.GetString(0);
                var existingStatus = existingReader.GetString(1);
                await existingReader.DisposeAsync();
                if (!string.Equals(existingFingerprint, request.RequestFingerprint ?? "",
                    StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(ct);
                    return LeaseCreateResult.IdempotencyConflict();
                }

                if (existingStatus is not ("aborted" or "expired"))
                {
                    await transaction.RollbackAsync(ct);
                    return LeaseCreateResult.Duplicate();
                }

                await using var reopen = connection.CreateCommand();
                reopen.Transaction = transaction;
                reopen.CommandText = """
                    UPDATE request_idempotency
                    SET request_id = $3, lease_token = $4, status = 'active', updated_at = now()
                    WHERE api_key_id = $1 AND idempotency_key = $2
                      AND status IN ('aborted', 'expired')
                    """;
                reopen.Parameters.AddWithValue(request.ApiKeyId);
                reopen.Parameters.AddWithValue(request.IdempotencyKey.Trim());
                reopen.Parameters.AddWithValue(request.RequestId);
                reopen.Parameters.AddWithValue(request.LeaseToken);
                await reopen.ExecuteNonQueryAsync(ct);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.HoldHandle)
            && !await accounting.TryReserveHoldAsync(
                connection, transaction, request.UserId, request.HoldHandle,
                request.LeaseToken, request.HoldAmount, ct))
        {
            await transaction.RollbackAsync(ct);
            return LeaseCreateResult.NoFunds();
        }

        var subscription = await TryReserveSubscriptionQuotaAsync(
            connection, transaction, request.UserId, request.HoldAmount, ct);
        if (!subscription.Allowed)
        {
            await transaction.RollbackAsync(ct);
            return LeaseCreateResult.QuotaExceeded();
        }
        if (subscription.SubscriptionId is not null)
        {
            await using var attach = connection.CreateCommand();
            attach.Transaction = transaction;
            attach.CommandText = """
                UPDATE request_leases
                SET subscription_id = $2, subscription_hold_amount = $3
                WHERE lease_token = $1
                """;
            attach.Parameters.AddWithValue(request.LeaseToken);
            attach.Parameters.AddWithValue(subscription.SubscriptionId.Value);
            attach.Parameters.AddWithValue(request.HoldAmount);
            await attach.ExecuteNonQueryAsync(ct);
        }

        await AppendLeaseEventAsync(connection, transaction, request.LeaseToken,
            "held", "platform", "balance hold reserved", null, ct);

        await transaction.CommitAsync(ct);
        return LeaseCreateResult.New();
    }

    public async Task<IdempotencyLookup> CheckIdempotencyAsync(long apiKeyId,
        string? idempotencyKey, string? requestFingerprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return IdempotencyLookup.Missing();
        await using var command = dataSource.CreateCommand("""
            SELECT request_fingerprint, status,
                   COALESCE(request_id, ''), COALESCE(lease_token, ''),
                   COALESCE(response_status_code, 0),
                   COALESCE(response_content_type, ''),
                   COALESCE(response_body, '')
            FROM request_idempotency
            WHERE api_key_id = $1 AND idempotency_key = $2
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(idempotencyKey.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return IdempotencyLookup.Missing();
        var existing = reader.GetString(0);
        var status = reader.GetString(1);
        if (status is "aborted" or "expired") return IdempotencyLookup.Missing();
        var requestId = reader.GetString(2);
        var leaseToken = reader.GetString(3);
        var responseStatusCode = reader.GetInt32(4);
        var responseContentType = reader.GetString(5);
        var responseBody = reader.GetString(6);
        return string.Equals(existing, requestFingerprint ?? "", StringComparison.Ordinal)
            ? IdempotencyLookup.Replay(responseStatusCode, responseContentType, responseBody)
                with { RequestId = requestId, LeaseToken = leaseToken }
            : IdempotencyLookup.FingerprintConflict();
    }

    public async Task<RequestLease?> GetByRequestIdAsync(string requestId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT lease_token, request_id, api_key_hash, api_key_id, user_id,
                   account_id, group_id, model, upstream_model, inbound_endpoint,
                   rate_multiplier, hold_handle, hold_amount, status, final_cost_usd, expires_at,
                   pricing_version, price_input_per_million, price_output_per_million,
                   price_cache_create_per_million, price_cache_read_per_million,
                   price_image_input_per_unit, price_image_output_per_unit,
                   price_video_per_second, price_realtime_per_minute,
                   subscription_id, subscription_hold_amount,
                   observed_model, price_source_provider, price_source_checksum,
                   price_search_per_query, price_audio_per_minute,
                   price_character_per_million, price_long_context_per_million
            FROM request_leases WHERE request_id = $1
            """);
        command.Parameters.AddWithValue(requestId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
    }

    public async Task<RequestLease?> GetByLeaseTokenAsync(string leaseToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseToken)) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT lease_token, request_id, api_key_hash, api_key_id, user_id,
                   account_id, group_id, model, upstream_model, inbound_endpoint,
                   rate_multiplier, hold_handle, hold_amount, status, final_cost_usd, expires_at,
                   pricing_version, price_input_per_million, price_output_per_million,
                   price_cache_create_per_million, price_cache_read_per_million,
                   price_image_input_per_unit, price_image_output_per_unit,
                   price_video_per_second, price_realtime_per_minute,
                   subscription_id, subscription_hold_amount,
                   observed_model, price_source_provider, price_source_checksum,
                   price_search_per_query, price_audio_per_minute,
                   price_character_per_million, price_long_context_per_million
            FROM request_leases WHERE lease_token = $1
            """);
        command.Parameters.AddWithValue(leaseToken);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
    }

    public async Task<WriteAck> CompleteAsync(LeaseCompletion completion, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var ack = await CompleteOnTransactionAsync(connection, transaction, completion,
            "platform", ct);
        if (ack.Accepted)
        {
            faults?.CrashIfConfigured("platform.before_settlement_commit", completion.LeaseToken);
            await transaction.CommitAsync(ct);
            faults?.CrashIfConfigured("platform.after_settlement_commit", completion.LeaseToken);
        }
        return ack;
    }

    private async Task<WriteAck> CompleteOnTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LeaseCompletion completion,
        string source,
        CancellationToken ct)
    {
        var lease = await GetForUpdateAsync(connection, transaction, completion.LeaseToken, ct);
        if (lease is null)
            return WriteAck.Error("lease_not_found");
        if (lease.Status == "completed")
            return WriteAck.DuplicateWrite();
        if (lease.Status is not ("held" or "forwarded" or "output_started"
            or "reconciliation_needed"))
            return WriteAck.Error($"lease_{lease.Status}");

        var normalized = completion with
        {
            InputTokens = Math.Max(0, completion.InputTokens),
            OutputTokens = Math.Max(0, completion.OutputTokens),
            CacheCreateTokens = Math.Max(0, completion.CacheCreateTokens),
            CacheReadTokens = Math.Max(0, completion.CacheReadTokens),
            DurationMs = Math.Max(0, completion.DurationMs),
            FirstTokenMs = Math.Max(0, completion.FirstTokenMs),
            InputImageCount = Math.Max(0, completion.InputImageCount),
            OutputImageCount = Math.Max(0, completion.OutputImageCount),
            VideoCount = Math.Max(0, completion.VideoCount),
            VideoDurationSeconds = Math.Max(0, completion.VideoDurationSeconds),
            RealtimeDurationMs = Math.Max(0, completion.RealtimeDurationMs),
            RealtimeFrames = Math.Max(0, completion.RealtimeFrames),
            ReasoningTokens = Math.Max(0, completion.ReasoningTokens),
            SearchQueryCount = Math.Max(0, completion.SearchQueryCount),
            AudioMinutes = Math.Max(0m, completion.AudioMinutes),
            CharacterCount = Math.Max(0, completion.CharacterCount),
            LongContextTokenCount = Math.Max(0, completion.LongContextTokenCount),
            PricingVersion = lease.PricingVersion ?? completion.PricingVersion,
            ResponseStatusCode = Math.Clamp(completion.ResponseStatusCode, 0, 999),
            ResponseContentType = completion.ResponseContentType.Length > 256
                ? completion.ResponseContentType[..256] : completion.ResponseContentType,
            ResponseBody = Encoding.UTF8.GetByteCount(completion.ResponseBody) <= 4 * 1024 * 1024
                ? completion.ResponseBody : "",
        };
        var price = lease.PriceSnapshot;
        if (price is null)
        {
            logger.LogWarning("Usage settlement deferred because lease {LeaseToken} has no price snapshot", lease.LeaseToken);
            return WriteAck.Error("pricing_snapshot_missing", retryable: true);
        }

        // Model mismatch detection: compare observed model against requested models
        var modelMismatchDetected = false;
        var modelMismatchBillingModel = "";
        var billingPrice = price;
        var observedModel = normalized.ObservedModel;
        if (!string.IsNullOrWhiteSpace(observedModel)
            && !string.Equals(observedModel, lease.Model, StringComparison.Ordinal)
            && !string.Equals(observedModel, lease.UpstreamModel, StringComparison.Ordinal))
        {
            modelMismatchDetected = true;
            if (pricing.TryGetPrice(observedModel, out var observedPrice))
            {
                var requestedCost = ComputeCost(lease, normalized, price);
                var observedCost = ComputeCost(lease, normalized, observedPrice);
                if (observedCost < requestedCost)
                {
                    // Observed model is cheaper - bill at observed price (consumer benefit)
                    billingPrice = observedPrice;
                    modelMismatchBillingModel = observedModel;
                    logger.LogWarning(
                        "Model mismatch on lease {LeaseToken}: requested={Requested}, observed={Observed}. Billing at cheaper observed price.",
                        lease.LeaseToken, lease.Model, observedModel);
                }
                else
                {
                    // Observed model is more expensive - bill at requested (cheaper) price (no auto-upgrade)
                    modelMismatchBillingModel = lease.Model;
                    logger.LogWarning(
                        "Model mismatch on lease {LeaseToken}: requested={Requested}, observed={Observed}. Billing at requested price (no auto-upgrade).",
                        lease.LeaseToken, lease.Model, observedModel);
                }
            }
            else
            {
                // Observed model has no price - bill at requested price (never zero)
                modelMismatchBillingModel = lease.Model;
                logger.LogWarning(
                    "Model mismatch on lease {LeaseToken}: requested={Requested}, observed={Observed} (no price). Billing at requested price.",
                    lease.LeaseToken, lease.Model, observedModel);
            }
        }

        var cost = ComputeCost(lease, normalized, billingPrice);

        await using (var usage = connection.CreateCommand())
        {
            usage.Transaction = transaction;
            usage.CommandText = """
                INSERT INTO usage_events (
                    lease_token, request_id, api_key_id, user_id, account_id, group_id,
                    model, upstream_model, inbound_endpoint, input_tokens, output_tokens,
                    cache_create_tokens, cache_read_tokens, cost_usd, duration_ms,
                    first_token_ms, status_code, stream, client_disconnect,
                    input_image_count, output_image_count, image_size, video_count,
                    video_resolution, video_duration_seconds, realtime_duration_ms,
                    realtime_frames, disconnect_reason, provider_usage_json,
                    reasoning_tokens, service_tier, upstream_endpoint, cancellation_reason,
                    media_operation_id, pricing_version,
                    observed_model, search_query_count, audio_minutes, character_count,
                    long_context_token_count, price_source_provider, price_source_checksum,
                    model_mismatch_detected, model_mismatch_billing_model)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,
                        $20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34,$35,
                        $36,$37,$38,$39,$40,$41,$42,$43,$44)
                """;
            AddUsageParameters(usage, lease, normalized, cost, includeEndpointAndStatus: true);
            AddUsageExtensions(usage, normalized, modelMismatchDetected, modelMismatchBillingModel);
            await usage.ExecuteNonQueryAsync(ct);
        }

        await using (var log = connection.CreateCommand())
        {
            log.Transaction = transaction;
            log.CommandText = """
                INSERT INTO usage_logs (
                    lease_token, request_id, api_key_id, user_id, account_id, group_id,
                    model, upstream_model, input_tokens, output_tokens, cache_create_tokens,
                    cache_read_tokens, cost_usd, duration_ms, first_token_ms, stream,
                    client_disconnect, input_image_count, output_image_count, image_size,
                    video_count, video_resolution, video_duration_seconds,
                    realtime_duration_ms, realtime_frames, disconnect_reason,
                    provider_usage_json, reasoning_tokens, service_tier,
                    upstream_endpoint, cancellation_reason, media_operation_id,
                    pricing_version,
                    observed_model, search_query_count, audio_minutes, character_count,
                    long_context_token_count, price_source_provider, price_source_checksum,
                    model_mismatch_detected, model_mismatch_billing_model)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,
                        $18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,
                        $34,$35,$36,$37,$38,$39,$40,$41,$42)
                """;
            AddUsageParameters(log, lease, normalized, cost, includeEndpointAndStatus: false);
            AddUsageExtensions(log, normalized, modelMismatchDetected, modelMismatchBillingModel);
            await log.ExecuteNonQueryAsync(ct);
        }

        await accounting.FinalizeHoldAsync(connection, transaction,
            lease.UserId, lease.HoldHandle, "committed", ct);
        if (cost > 0m)
        {
            var effect = await accounting.AppendEffectAsync(connection, transaction,
                new AccountingEffect(
                    lease.UserId, $"usage:{lease.LeaseToken}", "usage_debit", -cost,
                    LeaseToken: lease.LeaseToken), ct);
            if (effect.Status is AccountingEffectStatus.Conflict
                or AccountingEffectStatus.InsufficientFunds)
                throw new InvalidOperationException(
                    $"Usage accounting effect {effect.EffectId} was rejected: {effect.Status}");
        }

        await FinalizeSubscriptionQuotaAsync(connection, transaction, lease, cost, ct);

        await using (var finalize = connection.CreateCommand())
        {
            finalize.Transaction = transaction;
            finalize.CommandText = """
                UPDATE request_leases
                SET status = 'completed', final_cost_usd = $2, finalized_at = now()
                WHERE lease_token = $1
                """;
            finalize.Parameters.AddWithValue(lease.LeaseToken);
            finalize.Parameters.AddWithValue(cost);
            await finalize.ExecuteNonQueryAsync(ct);
        }

        await FinalizeIdempotencyAsync(connection, transaction, lease.LeaseToken, "completed", ct);
        await StoreIdempotencyResponseAsync(connection, transaction, lease.LeaseToken,
            normalized.ResponseStatusCode, normalized.ResponseContentType,
            normalized.ResponseBody, ct);
        await EnqueueAsync(connection, transaction, lease.LeaseToken, "complete", ct);
        await AppendLeaseEventAsync(connection, transaction, lease.LeaseToken,
            "completed", source, source == "platform" ? "usage settled" : "usage settled by operator",
            normalized.StatusCode is >= 100 and <= 999 ? normalized.StatusCode : null, ct);
        return WriteAck.Ok();
    }

    public async Task<ReconciliationResolutionResult> ResolveReconciliationAsync(
        long incidentId,
        long actorId,
        string idempotencyKey,
        ReconciliationResolutionRequest request,
        string ipAddress = "",
        CancellationToken ct = default)
    {
        if (!TryNormalizeResolution(incidentId, actorId, idempotencyKey, request,
                out var normalized, out var validationError))
            return new(ReconciliationResolutionStatus.Invalid, validationError);

        var fingerprint = ReconciliationResolutionFingerprint.Compute(incidentId, normalized);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        var incident = await ReadIncidentForUpdateAsync(connection, transaction, incidentId, ct);
        if (incident is null)
            return new(ReconciliationResolutionStatus.NotFound, "incident_not_found");

        var existing = await FindResolutionAsync(connection, transaction, idempotencyKey, ct);
        if (existing is not null)
        {
            var existingValue = existing.Value;
            if (!string.Equals(existingValue.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                return new(ReconciliationResolutionStatus.Conflict,
                    "resolution_idempotency_conflict", existingValue.Id, existingValue.LeaseToken,
                    existingValue.Action);

            await transaction.CommitAsync(ct);
            return new(ReconciliationResolutionStatus.Duplicate, "", existingValue.Id,
                existingValue.LeaseToken, existingValue.Action);
        }

        var incidentValue = incident.Value;
        if (!string.Equals(incidentValue.Status, "open", StringComparison.Ordinal))
            return new(ReconciliationResolutionStatus.Invalid, "incident_already_resolved");
        if (!string.Equals(incidentValue.Kind, "unknown_provider_charge", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(incidentValue.LeaseToken))
            return new(ReconciliationResolutionStatus.Invalid, "incident_not_operator_resolvable");

        var lease = await GetForUpdateAsync(connection, transaction, incidentValue.LeaseToken, ct);
        if (lease is null)
            return new(ReconciliationResolutionStatus.Invalid, "lease_not_found");
        if (!string.Equals(lease.Status, "reconciliation_needed", StringComparison.Ordinal))
            return new(ReconciliationResolutionStatus.Invalid, "lease_not_reconciliation_needed");
        if (normalized.Action == "release"
            && normalized.EvidenceType == "never_forwarded"
            && await HasDispatchEvidenceAsync(connection, transaction, lease.LeaseToken, ct))
            return new(ReconciliationResolutionStatus.Invalid, "release_evidence_conflict");

        decimal? cost = null;
        if (normalized.Action == "settle")
        {
            var completion = ToLeaseCompletion(lease.LeaseToken, normalized);
            var completionAck = await CompleteOnTransactionAsync(
                connection, transaction, completion, "operator", ct);
            if (!completionAck.Accepted)
                return new(ReconciliationResolutionStatus.Invalid, completionAck.ErrorCode);
            cost = await ReadLeaseCostAsync(connection, transaction, lease.LeaseToken, ct);
        }
        else
        {
            var providerStatusCode = normalized.EvidenceType == "provider_rejection"
                && normalized.StatusCode is >= 100 and <= 999
                ? normalized.StatusCode : (int?)null;
            await using var abort = connection.CreateCommand();
            abort.Transaction = transaction;
            abort.CommandText = """
                UPDATE request_leases
                SET status = 'aborted', abort_reason = $2,
                    provider_status_code = $3, finalized_at = now()
                WHERE lease_token = $1 AND status = 'reconciliation_needed'
                """;
            abort.Parameters.AddWithValue(lease.LeaseToken);
            abort.Parameters.AddWithValue($"operator_release:{normalized.Reason}");
            abort.Parameters.AddWithValue((object?)providerStatusCode ?? DBNull.Value);
            if (await abort.ExecuteNonQueryAsync(ct) != 1)
                return new(ReconciliationResolutionStatus.Invalid, "lease_state_changed");

            await accounting.FinalizeHoldAsync(connection, transaction,
                lease.UserId, lease.HoldHandle, "released", ct);
            await ReleaseSubscriptionQuotaAsync(connection, transaction, lease, ct);
            await FinalizeIdempotencyAsync(connection, transaction,
                lease.LeaseToken, "aborted", ct);
            await EnqueueAsync(connection, transaction, lease.LeaseToken, "abort", ct);
            await AppendLeaseEventAsync(connection, transaction, lease.LeaseToken,
                "aborted_no_charge", "operator",
                $"{normalized.EvidenceType}: {normalized.Evidence}", providerStatusCode, ct);
        }

        var resolutionId = await InsertResolutionAsync(connection, transaction,
            incidentId, lease.LeaseToken, actorId, idempotencyKey, fingerprint,
            normalized, ct);
        await MarkIncidentResolvedAsync(connection, transaction, incidentId, ct);
        await InsertResolutionAuditAsync(connection, transaction, incidentId,
            lease.LeaseToken, actorId, normalized, resolutionId, cost, ipAddress, ct);
        await transaction.CommitAsync(ct);
        return new(ReconciliationResolutionStatus.Applied, "", resolutionId,
            lease.LeaseToken, normalized.Action, cost);
    }

    private static bool TryNormalizeResolution(
        long incidentId,
        long actorId,
        string idempotencyKey,
        ReconciliationResolutionRequest request,
        out ReconciliationResolutionRequest normalized,
        out string error)
    {
        normalized = request with
        {
            Action = request.Action?.Trim().ToLowerInvariant() ?? "",
            EvidenceType = request.EvidenceType?.Trim().ToLowerInvariant() ?? "",
            Evidence = request.Evidence?.Trim() ?? "",
            Reason = request.Reason?.Trim() ?? "",
        };
        error = "";
        if (incidentId <= 0 || actorId <= 0)
            error = "invalid_identity";
        else if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 128)
            error = "invalid_idempotency_key";
        else if (normalized.Action is not ("settle" or "release"))
            error = "invalid_resolution_action";
        else if (normalized.Evidence.Length is < 3 or > 2000)
            error = "invalid_evidence";
        else if (normalized.Reason.Length is < 3 or > 500)
            error = "invalid_reason";
        else if (normalized.Action == "settle"
            && normalized.EvidenceType is not ("provider_usage" or "provider_invoice"
                or "operator_usage_review"))
            error = "invalid_settlement_evidence";
        else if (normalized.Action == "release"
            && normalized.EvidenceType is not ("never_forwarded" or "provider_rejection"
                or "provider_confirmed_no_charge"))
            error = "invalid_release_evidence";
        else if (normalized.Action == "release"
            && normalized.EvidenceType == "provider_rejection"
            && normalized.StatusCode is < 400 or > 599)
            error = "invalid_provider_rejection_status";
        else if (HasNegativeUsage(normalized))
            error = "negative_usage";
        else if (normalized.Action == "settle"
            && normalized.StatusCode is < 100 or > 999)
            error = "invalid_status_code";
        return error.Length == 0;
    }

    private static bool HasNegativeUsage(ReconciliationResolutionRequest request) =>
        request.InputTokens < 0 || request.OutputTokens < 0
        || request.CacheCreateTokens < 0 || request.CacheReadTokens < 0
        || request.DurationMs < 0 || request.FirstTokenMs < 0
        || request.InputImageCount < 0 || request.OutputImageCount < 0
        || request.VideoCount < 0 || request.VideoDurationSeconds < 0
        || request.RealtimeDurationMs < 0 || request.RealtimeFrames < 0
        || request.ReasoningTokens < 0;

    private static LeaseCompletion ToLeaseCompletion(
        string leaseToken, ReconciliationResolutionRequest request) => new(
        leaseToken, request.InputTokens, request.OutputTokens,
        request.CacheCreateTokens, request.CacheReadTokens, request.DurationMs,
        request.FirstTokenMs, request.StatusCode, request.Stream,
        request.ClientDisconnect, request.InputImageCount, request.OutputImageCount,
        request.ImageSize, request.VideoCount, request.VideoResolution,
        request.VideoDurationSeconds, request.RealtimeDurationMs, request.RealtimeFrames,
        request.DisconnectReason, request.ProviderUsageJson, request.ReasoningTokens,
        request.ServiceTier, request.UpstreamEndpoint, request.CancellationReason,
        request.MediaOperationId, ResponseStatusCode: request.ResponseStatusCode,
        ResponseContentType: request.ResponseContentType, ResponseBody: request.ResponseBody);

    private static async Task<(string Kind, string Status, string? LeaseToken)?>
        ReadIncidentForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            long incidentId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind, status, lease_token
            FROM accounting_reconciliation_incidents
            WHERE id = $1
            FOR UPDATE
            """;
        command.Parameters.AddWithValue(incidentId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<(long Id, string RequestFingerprint, string Action,
        string LeaseToken)?> FindResolutionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string idempotencyKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, request_fingerprint, action, lease_token
            FROM accounting_reconciliation_resolutions
            WHERE idempotency_key = $1
            FOR UPDATE
            """;
        command.Parameters.AddWithValue(idempotencyKey.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private static async Task<decimal?> ReadLeaseCostAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string leaseToken, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT final_cost_usd FROM request_leases WHERE lease_token = $1";
        command.Parameters.AddWithValue(leaseToken);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull or null ? null : Convert.ToDecimal(value);
    }

    private static async Task<bool> HasDispatchEvidenceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string leaseToken, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM request_lease_events
                WHERE lease_token = $1 AND event_type IN ('forwarded', 'output_started'))
            """;
        command.Parameters.AddWithValue(leaseToken);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task<long> InsertResolutionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long incidentId, string leaseToken, long actorId, string idempotencyKey,
        string fingerprint, ReconciliationResolutionRequest request,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO accounting_reconciliation_resolutions(
                incident_id, lease_token, action, evidence_type, evidence, reason,
                actor_user_id, idempotency_key, request_fingerprint, usage_payload)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            RETURNING id
            """;
        command.Parameters.AddWithValue(incidentId);
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(request.Action);
        command.Parameters.AddWithValue(request.EvidenceType);
        command.Parameters.AddWithValue(request.Evidence);
        command.Parameters.AddWithValue(request.Reason);
        command.Parameters.AddWithValue(actorId);
        command.Parameters.AddWithValue(idempotencyKey.Trim());
        command.Parameters.AddWithValue(fingerprint);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = JsonSerializer.Serialize(request),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
        });
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task MarkIncidentResolvedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long incidentId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE accounting_reconciliation_incidents
            SET status = 'resolved', resolved_at = now()
            WHERE id = $1 AND status = 'open'
            """;
        command.Parameters.AddWithValue(incidentId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Reconciliation incident changed while resolving");
    }

    private static async Task InsertResolutionAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long incidentId, string leaseToken, long actorId,
        ReconciliationResolutionRequest request, long resolutionId,
        decimal? cost, string ipAddress, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_logs(user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, 'reconciliation.resolve', 'lease', $2, $3, $4)
            """;
        command.Parameters.AddWithValue(actorId);
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(new
        {
            incident_id = incidentId,
            resolution_id = resolutionId,
            action = request.Action,
            evidence_type = request.EvidenceType,
            reason = request.Reason,
            cost_usd = cost,
        }));
        command.Parameters.AddWithValue(ipAddress.Length > 100 ? ipAddress[..100] : ipAddress);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<WriteAck> RecordEvidenceAsync(string leaseToken, LeaseEvidenceStage stage,
        string source = "gateway", string detail = "", CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var lease = await GetForUpdateAsync(connection, transaction, leaseToken, ct);
        if (lease is null) return WriteAck.Error("lease_not_found");

        var target = stage == LeaseEvidenceStage.Forwarded ? "forwarded" : "output_started";
        if (lease.Status == target
            || (stage == LeaseEvidenceStage.Forwarded && lease.Status == "output_started"))
        {
            await transaction.CommitAsync(ct);
            return WriteAck.DuplicateWrite();
        }
        if (lease.Status is "completed" or "aborted" or "expired" or "reconciliation_needed")
            return WriteAck.Error($"lease_{lease.Status}");
        if (stage == LeaseEvidenceStage.Forwarded && lease.Status != "held")
            return WriteAck.Error($"invalid_evidence_transition_{lease.Status}_to_forwarded");
        if (stage == LeaseEvidenceStage.OutputStarted && lease.Status != "forwarded")
            return WriteAck.Error($"invalid_evidence_transition_{lease.Status}_to_output_started");

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = stage == LeaseEvidenceStage.Forwarded
                ? "UPDATE request_leases SET status = 'forwarded', forwarded_at = now() WHERE lease_token = $1"
                : "UPDATE request_leases SET status = 'output_started', output_started_at = now() WHERE lease_token = $1";
            command.Parameters.AddWithValue(leaseToken);
            await command.ExecuteNonQueryAsync(ct);
        }
        await AppendLeaseEventAsync(connection, transaction, leaseToken, target,
            source, detail, null, ct);
        await transaction.CommitAsync(ct);
        return WriteAck.Ok();
    }

    public async Task<WriteAck> AbortAsync(string leaseToken, string reason,
        LeaseAbortDisposition disposition = LeaseAbortDisposition.NoCharge,
        int? providerStatusCode = null,
        CancellationToken ct = default,
        string source = "platform")
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var lease = await GetForUpdateAsync(connection, transaction, leaseToken, ct);
        if (lease is null)
            return WriteAck.Error("lease_not_found");
        if (lease.Status is "aborted" or "expired")
            return WriteAck.DuplicateWrite();
        if (lease.Status == "reconciliation_needed")
            return disposition == LeaseAbortDisposition.Unknown
                ? WriteAck.DuplicateWrite() : WriteAck.Error("lease_reconciliation_needed");
        if (lease.Status == "completed")
            return WriteAck.Error("lease_completed");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var unknown = disposition == LeaseAbortDisposition.Unknown;
        command.CommandText = unknown
            ? """
                UPDATE request_leases
                SET status = 'reconciliation_needed', abort_reason = $2,
                    provider_status_code = $3, reconciliation_needed_at = now()
                WHERE lease_token = $1
                """
            : """
                UPDATE request_leases
                SET status = 'aborted', abort_reason = $2,
                    provider_status_code = $3, finalized_at = now()
                WHERE lease_token = $1
                """;
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(reason.Length > 500 ? reason[..500] : reason);
        command.Parameters.AddWithValue((object?)providerStatusCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        if (!unknown)
        {
            await accounting.FinalizeHoldAsync(connection, transaction,
                lease.UserId, lease.HoldHandle, "released", ct);
            await ReleaseSubscriptionQuotaAsync(connection, transaction, lease, ct);
        }
        await FinalizeIdempotencyAsync(connection, transaction, lease.LeaseToken,
            unknown ? "reconciliation_needed" : "aborted", ct);
        await EnqueueAsync(connection, transaction, leaseToken, unknown ? "reconcile" : "abort", ct);
        await AppendLeaseEventAsync(connection, transaction, leaseToken,
            unknown ? "aborted_unknown" : "aborted_no_charge", source, reason,
            providerStatusCode, ct);
        if (unknown)
            await AppendLeaseEventAsync(connection, transaction, leaseToken,
                "reconciliation_needed", "platform", "Provider charge outcome is unknown",
                providerStatusCode, ct);
        await transaction.CommitAsync(ct);
        return WriteAck.Ok();
    }

    public Task<WriteAck> AbortAsync(string leaseToken, string reason, CancellationToken ct) =>
        AbortAsync(leaseToken, reason, LeaseAbortDisposition.NoCharge, null, ct);

    public async Task<int> ExpireActiveAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var expired = new List<(string Token, string Status, long UserId, string? HoldHandle,
            long? SubscriptionId, decimal SubscriptionHoldAmount)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT lease_token, status, user_id, hold_handle,
                       subscription_id, subscription_hold_amount
                FROM request_leases
                WHERE status IN ('held', 'forwarded', 'output_started')
                  AND expires_at <= now()
                ORDER BY user_id, lease_token
                LIMIT 500
                FOR UPDATE SKIP LOCKED
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                expired.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetDecimal(5)));
        }
        foreach (var item in expired)
        {
            var safe = item.Status == "held";
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = safe
                ? """
                    UPDATE request_leases
                    SET status = 'expired', abort_reason = 'lease_ttl_before_forward', finalized_at = now()
                    WHERE lease_token = $1
                    """
                : """
                    UPDATE request_leases
                    SET status = 'reconciliation_needed',
                        abort_reason = 'lease_ttl_unknown_provider_charge',
                        reconciliation_needed_at = now()
                    WHERE lease_token = $1
                    """;
            update.Parameters.AddWithValue(item.Token);
            await update.ExecuteNonQueryAsync(ct);
            if (safe)
            {
                await accounting.FinalizeHoldAsync(connection, transaction,
                    item.UserId, item.HoldHandle, "released", ct);
                await ReleaseSubscriptionQuotaAsync(connection, transaction,
                    item.SubscriptionId, item.SubscriptionHoldAmount, ct: ct);
            }
            await FinalizeIdempotencyAsync(connection, transaction,
                item.Token, safe ? "expired" : "reconciliation_needed", ct);
            await EnqueueAsync(connection, transaction, item.Token, safe ? "expire" : "reconcile", ct);
            await AppendLeaseEventAsync(connection, transaction, item.Token,
                safe ? "expired" : "reconciliation_needed", "lease-expiry",
                safe ? "request was never forwarded" : $"expired after {item.Status}", null, ct);
        }
        await transaction.CommitAsync(ct);
        return expired.Count;
    }

    public async Task<IReadOnlyList<ClaimedOutboxItem>> ClaimOutboxBatchAsync(
        string workerId, int batchSize = 50, CancellationToken ct = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 500);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH candidates AS (
                SELECT o.id, o.lease_token
                FROM usage_outbox o
                WHERE o.processed_at IS NULL
                  AND o.dead_lettered_at IS NULL
                  AND o.next_attempt_at <= now()
                  AND (o.claimed_until IS NULL OR o.claimed_until < now())
                ORDER BY o.id
                LIMIT $1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE usage_outbox o
            SET claimed_by = $2,
                claimed_until = now() + make_interval(secs => $3)
            FROM candidates c
            JOIN request_leases l ON l.lease_token = c.lease_token
            WHERE o.id = c.id
            RETURNING o.id, o.lease_token, o.event_type, o.attempts,
                      l.request_id, l.api_key_hash, l.api_key_id, l.user_id,
                      l.account_id, l.group_id, l.model, l.upstream_model,
                      l.inbound_endpoint, l.rate_multiplier, l.hold_handle,
                      l.hold_amount, l.status, l.final_cost_usd, l.expires_at,
                      l.pricing_version, l.price_input_per_million, l.price_output_per_million,
                      l.price_cache_create_per_million, l.price_cache_read_per_million,
                      l.price_image_input_per_unit, l.price_image_output_per_unit,
                      l.price_video_per_second, l.price_realtime_per_minute,
                      l.subscription_id, l.subscription_hold_amount,
                      l.observed_model, l.price_source_provider, l.price_source_checksum,
                      l.price_search_per_query, l.price_audio_per_minute,
                      l.price_character_per_million, l.price_long_context_per_million
            """;
        command.Parameters.AddWithValue(batchSize);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(30);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<ClaimedOutboxItem>();
        while (await reader.ReadAsync(ct))
        {
            var item = new OutboxItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
            results.Add(new ClaimedOutboxItem(item, ReadLease(reader, 1, detailsOffset: 3)));
        }
        return results;
    }

    public async Task MarkProcessedAsync(long id, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE usage_outbox SET processed_at = now(), last_error = NULL, claimed_by = NULL, claimed_until = NULL WHERE id = $1");
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkRetryAsync(OutboxItem item, Exception exception,
        CancellationToken ct = default)
    {
        var delaySeconds = Math.Min(300, 1 << Math.Min(item.Attempts, 8));
        await using var command = dataSource.CreateCommand("""
            UPDATE usage_outbox
            SET attempts = attempts + 1,
                next_attempt_at = now() + make_interval(secs => $2),
                last_error = $3,
                claimed_by = NULL,
                claimed_until = NULL
            WHERE id = $1
            """);
        command.Parameters.AddWithValue(item.Id);
        command.Parameters.AddWithValue(delaySeconds);
        var error = exception.Message;
        command.Parameters.AddWithValue(error.Length > 1000 ? error[..1000] : error);
        await command.ExecuteNonQueryAsync(ct);
        logger.LogWarning(exception, "Lease outbox {OutboxId} retry in {DelaySeconds}s", item.Id, delaySeconds);
    }

    // Financial settlement events are never automatically discarded. A prior
    // process version could have dead-lettered an event, so recover those rows
    // before workers start claiming new work after a restart.
    public async Task<int> RequeueUnprocessedDeadLettersAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE usage_outbox
            SET dead_lettered_at = NULL,
                next_attempt_at = now(),
                claimed_by = NULL,
                claimed_until = NULL
            WHERE processed_at IS NULL AND dead_lettered_at IS NOT NULL
            """);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeadLetterAsync(OutboxItem item, string reason, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE usage_outbox
            SET dead_lettered_at = now(), last_error = $2,
                claimed_by = NULL, claimed_until = NULL
            WHERE id = $1 AND processed_at IS NULL
            """);
        command.Parameters.AddWithValue(item.Id);
        command.Parameters.AddWithValue(reason.Length > 1000 ? reason[..1000] : reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(bool Allowed, long? SubscriptionId)> TryReserveSubscriptionQuotaAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        decimal holdAmount, CancellationToken ct)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id, quota_granted_usd, quota_used_usd, quota_reserved_usd
            FROM user_subscriptions
            WHERE user_id = $1 AND status = 'active'
              AND (expires_at IS NULL OR expires_at > now())
            ORDER BY expires_at DESC NULLS LAST, id DESC
            LIMIT 1
            FOR UPDATE
            """;
        select.Parameters.AddWithValue(userId);
        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (true, null);

        var subscriptionId = reader.GetInt64(0);
        var granted = reader.GetDecimal(1);
        var used = reader.GetDecimal(2);
        var reserved = reader.GetDecimal(3);
        var available = granted - used - reserved;
        await reader.DisposeAsync();
        if (available < holdAmount)
            return (false, subscriptionId);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE user_subscriptions
            SET quota_reserved_usd = quota_reserved_usd + $2
            WHERE id = $1
            """;
        update.Parameters.AddWithValue(subscriptionId);
        update.Parameters.AddWithValue(holdAmount);
        await update.ExecuteNonQueryAsync(ct);
        return (true, subscriptionId);
    }

    private static Task FinalizeSubscriptionQuotaAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        RequestLease lease, decimal cost, CancellationToken ct) =>
        ReleaseSubscriptionQuotaAsync(connection, transaction, lease.SubscriptionId,
            lease.SubscriptionHoldAmount, cost, ct);

    private static Task ReleaseSubscriptionQuotaAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        RequestLease lease, CancellationToken ct) =>
        ReleaseSubscriptionQuotaAsync(connection, transaction, lease.SubscriptionId,
            lease.SubscriptionHoldAmount, 0m, ct);

    private static async Task ReleaseSubscriptionQuotaAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long? subscriptionId, decimal reservedAmount, decimal usedAmount = 0m,
        CancellationToken ct = default)
    {
        if (subscriptionId is null || (reservedAmount <= 0m && usedAmount <= 0m))
            return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE user_subscriptions
            SET quota_reserved_usd = GREATEST(quota_reserved_usd - $2, 0),
                quota_used_usd = quota_used_usd + $3
            WHERE id = $1
            """;
        command.Parameters.AddWithValue(subscriptionId.Value);
        command.Parameters.AddWithValue(reservedAmount);
        command.Parameters.AddWithValue(usedAmount);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException($"Subscription {subscriptionId} disappeared during lease settlement");
    }

    private static decimal ComputeCost(RequestLease lease, LeaseCompletion usage, ModelPrice price)
    {
        var cost = usage.InputTokens * price.InputPerMillion / 1_000_000m
            + usage.OutputTokens * price.OutputPerMillion / 1_000_000m
            + usage.CacheCreateTokens * price.CacheCreatePerMillion / 1_000_000m
            + usage.CacheReadTokens * price.CacheReadPerMillion / 1_000_000m
            + usage.InputImageCount * price.ImageInputPerUnit
            + usage.OutputImageCount * price.ImageOutputPerUnit
            + usage.VideoDurationSeconds * price.VideoPerSecond
            + usage.RealtimeDurationMs * price.RealtimePerMinute / 60_000m
            + usage.SearchQueryCount * price.SearchPerQuery
            + usage.AudioMinutes * price.AudioPerMinute
            + usage.CharacterCount * price.CharacterPerMillion / 1_000_000m
            + usage.LongContextTokenCount * price.LongContextPerMillion / 1_000_000m;
        return decimal.Round(cost * lease.RateMultiplier, 8, MidpointRounding.AwayFromZero);
    }

    private static void AddUsageParameters(NpgsqlCommand command, RequestLease lease,
        LeaseCompletion usage, decimal cost, bool includeEndpointAndStatus)
    {
        command.Parameters.AddWithValue(lease.LeaseToken);
        command.Parameters.AddWithValue(lease.RequestId);
        command.Parameters.AddWithValue(lease.ApiKeyId);
        command.Parameters.AddWithValue(lease.UserId);
        command.Parameters.AddWithValue(lease.AccountId);
        command.Parameters.AddWithValue(lease.GroupId);
        command.Parameters.AddWithValue(lease.Model);
        command.Parameters.AddWithValue(lease.UpstreamModel);
        if (includeEndpointAndStatus)
            command.Parameters.AddWithValue(lease.InboundEndpoint);
        command.Parameters.AddWithValue(usage.InputTokens);
        command.Parameters.AddWithValue(usage.OutputTokens);
        command.Parameters.AddWithValue(usage.CacheCreateTokens);
        command.Parameters.AddWithValue(usage.CacheReadTokens);
        command.Parameters.AddWithValue(cost);
        command.Parameters.AddWithValue(usage.DurationMs);
        command.Parameters.AddWithValue(usage.FirstTokenMs);
        if (includeEndpointAndStatus)
            command.Parameters.AddWithValue(usage.StatusCode);
        command.Parameters.AddWithValue(usage.Stream);
        command.Parameters.AddWithValue(usage.ClientDisconnect);
    }

    private static void AddUsageExtensions(NpgsqlCommand command, LeaseCompletion usage,
        bool modelMismatchDetected = false, string modelMismatchBillingModel = "")
    {
        command.Parameters.AddWithValue(usage.InputImageCount);
        command.Parameters.AddWithValue(usage.OutputImageCount);
        command.Parameters.AddWithValue(usage.ImageSize);
        command.Parameters.AddWithValue(usage.VideoCount);
        command.Parameters.AddWithValue(usage.VideoResolution);
        command.Parameters.AddWithValue(usage.VideoDurationSeconds);
        command.Parameters.AddWithValue(usage.RealtimeDurationMs);
        command.Parameters.AddWithValue(usage.RealtimeFrames);
        command.Parameters.AddWithValue(usage.DisconnectReason);
        command.Parameters.AddWithValue(usage.ProviderUsageJson.Length > 1_048_576
            ? usage.ProviderUsageJson[..1_048_576] : usage.ProviderUsageJson);
        command.Parameters.AddWithValue(usage.ReasoningTokens);
        command.Parameters.AddWithValue(usage.ServiceTier);
        command.Parameters.AddWithValue(usage.UpstreamEndpoint);
        command.Parameters.AddWithValue(usage.CancellationReason);
        command.Parameters.AddWithValue(usage.MediaOperationId);
        command.Parameters.AddWithValue(usage.PricingVersion);
        // New response-model contract columns
        command.Parameters.AddWithValue(usage.ObservedModel);
        command.Parameters.AddWithValue(usage.SearchQueryCount);
        command.Parameters.AddWithValue(usage.AudioMinutes);
        command.Parameters.AddWithValue(usage.CharacterCount);
        command.Parameters.AddWithValue(usage.LongContextTokenCount);
        command.Parameters.AddWithValue(""); // price_source_provider (from lease)
        command.Parameters.AddWithValue(""); // price_source_checksum (from lease)
        command.Parameters.AddWithValue(modelMismatchDetected);
        command.Parameters.AddWithValue(modelMismatchBillingModel);
    }

    private static async Task<RequestLease?> GetForUpdateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string leaseToken, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT lease_token, request_id, api_key_hash, api_key_id, user_id,
                   account_id, group_id, model, upstream_model, inbound_endpoint,
                   rate_multiplier, hold_handle, hold_amount, status, final_cost_usd, expires_at,
                   pricing_version, price_input_per_million, price_output_per_million,
                   price_cache_create_per_million, price_cache_read_per_million,
                   price_image_input_per_unit, price_image_output_per_unit,
                   price_video_per_second, price_realtime_per_minute,
                   subscription_id, subscription_hold_amount,
                   observed_model, price_source_provider, price_source_checksum,
                   price_search_per_query, price_audio_per_minute,
                   price_character_per_million, price_long_context_per_million
            FROM request_leases WHERE lease_token = $1 FOR UPDATE
            """;
        command.Parameters.AddWithValue(leaseToken);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
    }

    private static RequestLease ReadLease(NpgsqlDataReader reader, int tokenOffset = 0,
        int detailsOffset = 0)
    {
        var i = detailsOffset;
        var token = reader.GetString(tokenOffset);
        return new RequestLease(
            token,
            reader.GetString(i + 1), reader.GetString(i + 2), reader.GetInt64(i + 3),
            reader.GetInt64(i + 4), reader.GetInt64(i + 5), reader.GetInt64(i + 6),
            reader.GetString(i + 7), reader.GetString(i + 8), reader.GetString(i + 9),
            reader.GetDecimal(i + 10), reader.IsDBNull(i + 11) ? null : reader.GetString(i + 11),
            reader.GetDecimal(i + 12), reader.GetString(i + 13),
            reader.IsDBNull(i + 14) ? null : reader.GetDecimal(i + 14), reader.GetDateTime(i + 15),
            reader.IsDBNull(i + 16) ? null : reader.GetString(i + 16),
            reader.IsDBNull(i + 17) ? null : reader.GetDecimal(i + 17),
            reader.IsDBNull(i + 18) ? null : reader.GetDecimal(i + 18),
            reader.IsDBNull(i + 19) ? null : reader.GetDecimal(i + 19),
            reader.IsDBNull(i + 20) ? null : reader.GetDecimal(i + 20),
            reader.IsDBNull(i + 21) ? null : reader.GetDecimal(i + 21),
            reader.IsDBNull(i + 22) ? null : reader.GetDecimal(i + 22),
            reader.IsDBNull(i + 23) ? null : reader.GetDecimal(i + 23),
            reader.IsDBNull(i + 24) ? null : reader.GetDecimal(i + 24),
            // Record ctor: PriceSearchPerQuery(col30), PriceAudioPerMinute(col31),
            // PriceCharacterPerMillion(col32), PriceLongContextPerMillion(col33),
            // ObservedModel(col27), PriceSourceProvider(col28), PriceSourceChecksum(col29),
            // SubscriptionId(col25), SubscriptionHoldAmount(col26)
            reader.IsDBNull(i + 30) ? null : reader.GetDecimal(i + 30),
            reader.IsDBNull(i + 31) ? null : reader.GetDecimal(i + 31),
            reader.IsDBNull(i + 32) ? null : reader.GetDecimal(i + 32),
            reader.IsDBNull(i + 33) ? null : reader.GetDecimal(i + 33),
            reader.GetString(i + 27),
            reader.GetString(i + 28),
            reader.GetString(i + 29),
            reader.IsDBNull(i + 25) ? null : reader.GetInt64(i + 25),
            reader.GetDecimal(i + 26));
    }

    private static async Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string leaseToken, string eventType, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage_outbox(lease_token, event_type) VALUES ($1, $2)
            ON CONFLICT (lease_token, event_type) DO NOTHING
            """;
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(eventType);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AppendLeaseEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string leaseToken,
        string eventType,
        string source,
        string detail,
        int? providerStatusCode,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO request_lease_events(
                lease_token, event_type, source, detail, provider_status_code)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (lease_token, event_type) DO NOTHING
            """;
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(eventType);
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
        command.Parameters.AddWithValue(normalizedSource.Length > 100
            ? normalizedSource[..100] : normalizedSource);
        command.Parameters.AddWithValue(detail.Length > 500 ? detail[..500] : detail);
        command.Parameters.AddWithValue((object?)providerStatusCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task FinalizeIdempotencyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string leaseToken, string status, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE request_idempotency
            SET status = $2, updated_at = now()
            WHERE lease_token = $1
              AND status IN ('active', 'reconciliation_needed')
            """;
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task StoreIdempotencyResponseAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string leaseToken, int statusCode,
        string contentType, string body, CancellationToken ct)
    {
        if (statusCode <= 0 || string.IsNullOrEmpty(body)) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE request_idempotency
            SET response_status_code = $2,
                response_content_type = $3,
                response_body = $4,
                completed_at = now(),
                updated_at = now()
            WHERE lease_token = $1 AND status = 'completed'
            """;
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(statusCode);
        command.Parameters.AddWithValue(contentType);
        command.Parameters.AddWithValue(body);
        await command.ExecuteNonQueryAsync(ct);
    }
}
