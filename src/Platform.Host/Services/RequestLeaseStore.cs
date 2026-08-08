using System.Data;
using System.Text;
using Npgsql;

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
    DateTime ExpiresAt);

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
    string RequestFingerprint = "");

public sealed record LeaseCreateResult(bool Created, bool Replay, bool Conflict)
{
    public static LeaseCreateResult New() => new(true, false, false);
    public static LeaseCreateResult Duplicate() => new(false, true, false);
    public static LeaseCreateResult IdempotencyConflict() => new(false, false, true);
}

public sealed record IdempotencyLookup(
    bool Found,
    bool Conflict,
    int ResponseStatusCode,
    string ResponseContentType,
    string ResponseBody)
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
    string ResponseBody = "");

public sealed record WriteAck(bool Accepted, bool Duplicate, bool Retryable, string ErrorCode)
{
    public static WriteAck Ok() => new(true, false, false, "");
    public static WriteAck DuplicateWrite() => new(true, true, false, "");
    public static WriteAck Error(string code, bool retryable = false) =>
        new(false, false, retryable, code);
}

public sealed record OutboxItem(long Id, string LeaseToken, string EventType, int Attempts);

public sealed record ClaimedOutboxItem(OutboxItem Item, RequestLease Lease);

public sealed class RequestLeaseStore(
    NpgsqlDataSource dataSource,
    ModelPricingService pricing,
    ILogger<RequestLeaseStore> logger)
{
    public async Task<bool> CreateAsync(LeaseCreateRequest request, CancellationToken ct = default)
        => (await CreateDetailedAsync(request, ct)).Created;

    public async Task<LeaseCreateResult> CreateDetailedAsync(LeaseCreateRequest request,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO request_leases (
                lease_token, request_id, api_key_hash, api_key_id, user_id,
                account_id, group_id, model, upstream_model, inbound_endpoint,
                rate_multiplier, hold_handle, hold_amount, status, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, 'active', $14)
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

        if (!string.IsNullOrWhiteSpace(request.HoldHandle))
        {
            await using var hold = connection.CreateCommand();
            hold.Transaction = transaction;
            hold.CommandText = """
                INSERT INTO balance_holds (hold_id, user_id, lease_token, amount, status)
                VALUES ($1, $2, $3, $4, 'active')
                ON CONFLICT (hold_id) DO NOTHING
                """;
            hold.Parameters.AddWithValue(request.HoldHandle);
            hold.Parameters.AddWithValue(request.UserId);
            hold.Parameters.AddWithValue(request.LeaseToken);
            hold.Parameters.AddWithValue(request.HoldAmount);
            await hold.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return LeaseCreateResult.New();
    }

    public async Task<IdempotencyLookup> CheckIdempotencyAsync(long apiKeyId,
        string? idempotencyKey, string? requestFingerprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return IdempotencyLookup.Missing();
        await using var command = dataSource.CreateCommand("""
            SELECT request_fingerprint, status,
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
        var responseStatusCode = reader.GetInt32(2);
        var responseContentType = reader.GetString(3);
        var responseBody = reader.GetString(4);
        return string.Equals(existing, requestFingerprint ?? "", StringComparison.Ordinal)
            ? IdempotencyLookup.Replay(responseStatusCode, responseContentType, responseBody)
            : IdempotencyLookup.FingerprintConflict();
    }

    public async Task<WriteAck> CompleteAsync(LeaseCompletion completion, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var lease = await GetForUpdateAsync(connection, transaction, completion.LeaseToken, ct);
        if (lease is null)
            return WriteAck.Error("lease_not_found");
        if (lease.Status == "completed")
            return WriteAck.DuplicateWrite();
        if (lease.Status != "active")
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
            ResponseStatusCode = Math.Clamp(completion.ResponseStatusCode, 0, 999),
            ResponseContentType = completion.ResponseContentType.Length > 256
                ? completion.ResponseContentType[..256] : completion.ResponseContentType,
            ResponseBody = Encoding.UTF8.GetByteCount(completion.ResponseBody) <= 4 * 1024 * 1024
                ? completion.ResponseBody : "",
        };
        if (!pricing.TryGetPrice(lease.Model, out var price))
        {
            logger.LogWarning("Usage settlement deferred because pricing is missing for model {Model}", lease.Model);
            return WriteAck.Error("pricing_unavailable", retryable: true);
        }
        var cost = ComputeCost(lease, normalized, price);

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
                    media_operation_id, pricing_version)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,
                        $20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34,$35)
                """;
            AddUsageParameters(usage, lease, normalized, cost, includeEndpointAndStatus: true);
            AddUsageExtensions(usage, normalized);
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
                    pricing_version)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,
                        $18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33)
                """;
            AddUsageParameters(log, lease, normalized, cost, includeEndpointAndStatus: false);
            AddUsageExtensions(log, normalized);
            await log.ExecuteNonQueryAsync(ct);
        }

        if (cost > 0m)
        {
            await using var ledger = connection.CreateCommand();
            ledger.Transaction = transaction;
            ledger.CommandText = """
                INSERT INTO balance_ledger (
                    user_id, reference, amount, lease_token, entry_type)
                VALUES ($1, $2, $3, $4, 'usage_debit')
                ON CONFLICT (lease_token, entry_type) DO NOTHING
                """;
            ledger.Parameters.AddWithValue(lease.UserId);
            ledger.Parameters.AddWithValue($"usage:{lease.LeaseToken}");
            ledger.Parameters.AddWithValue(-cost);
            ledger.Parameters.AddWithValue(lease.LeaseToken);
            await ledger.ExecuteNonQueryAsync(ct);
        }

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

        await FinalizeHoldAsync(connection, transaction, lease.HoldHandle, "committed", ct);
        await FinalizeIdempotencyAsync(connection, transaction, lease.LeaseToken, "completed", ct);
        await StoreIdempotencyResponseAsync(connection, transaction, lease.LeaseToken,
            normalized.ResponseStatusCode, normalized.ResponseContentType,
            normalized.ResponseBody, ct);
        await EnqueueAsync(connection, transaction, lease.LeaseToken, "complete", ct);
        await transaction.CommitAsync(ct);
        return WriteAck.Ok();
    }

    public async Task<WriteAck> AbortAsync(string leaseToken, string reason,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var lease = await GetForUpdateAsync(connection, transaction, leaseToken, ct);
        if (lease is null)
            return WriteAck.Error("lease_not_found");
        if (lease.Status is "aborted" or "expired")
            return WriteAck.DuplicateWrite();
        if (lease.Status == "completed")
            return WriteAck.Error("lease_completed");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE request_leases
            SET status = 'aborted', abort_reason = $2, finalized_at = now()
            WHERE lease_token = $1
            """;
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(reason.Length > 500 ? reason[..500] : reason);
        await command.ExecuteNonQueryAsync(ct);
        await FinalizeHoldAsync(connection, transaction, lease.HoldHandle, "released", ct);
        await FinalizeIdempotencyAsync(connection, transaction, lease.LeaseToken, "aborted", ct);
        await EnqueueAsync(connection, transaction, leaseToken, "abort", ct);
        await transaction.CommitAsync(ct);
        return WriteAck.Ok();
    }

    public async Task<int> ExpireActiveAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var expired = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE request_leases
                SET status = 'expired', abort_reason = 'lease_ttl', finalized_at = now()
                WHERE status = 'active' AND expires_at <= now()
                RETURNING lease_token
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                expired.Add(reader.GetString(0));
        }
        foreach (var token in expired)
        {
            await FinalizeHoldByLeaseAsync(connection, transaction, token, "released", ct);
            await FinalizeIdempotencyAsync(connection, transaction, token, "expired", ct);
            await EnqueueAsync(connection, transaction, token, "expire", ct);
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
                      l.hold_amount, l.status, l.final_cost_usd, l.expires_at
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
                claimed_until = NULL,
                dead_lettered_at = CASE WHEN attempts + 1 >= 25 THEN now() ELSE dead_lettered_at END
            WHERE id = $1
            """);
        command.Parameters.AddWithValue(item.Id);
        command.Parameters.AddWithValue(delaySeconds);
        var error = exception.Message;
        command.Parameters.AddWithValue(error.Length > 1000 ? error[..1000] : error);
        await command.ExecuteNonQueryAsync(ct);
        logger.LogWarning(exception, "Lease outbox {OutboxId} retry in {DelaySeconds}s", item.Id, delaySeconds);
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

    private static decimal ComputeCost(RequestLease lease, LeaseCompletion usage, ModelPrice price)
    {
        var cost = usage.InputTokens * price.InputPerMillion / 1_000_000m
            + usage.OutputTokens * price.OutputPerMillion / 1_000_000m
            + usage.CacheCreateTokens * price.CacheCreatePerMillion / 1_000_000m
            + usage.CacheReadTokens * price.CacheReadPerMillion / 1_000_000m
            + usage.InputImageCount * price.ImageInputPerUnit
            + usage.OutputImageCount * price.ImageOutputPerUnit
            + usage.VideoDurationSeconds * price.VideoPerSecond
            + usage.RealtimeDurationMs * price.RealtimePerMinute / 60_000m;
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

    private static void AddUsageExtensions(NpgsqlCommand command, LeaseCompletion usage)
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
    }

    private static async Task<RequestLease?> GetForUpdateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string leaseToken, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT lease_token, request_id, api_key_hash, api_key_id, user_id,
                   account_id, group_id, model, upstream_model, inbound_endpoint,
                   rate_multiplier, hold_handle, hold_amount, status, final_cost_usd, expires_at
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
            reader.IsDBNull(i + 14) ? null : reader.GetDecimal(i + 14), reader.GetDateTime(i + 15));
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

    private static async Task FinalizeHoldAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string? holdId, string status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(holdId)) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE balance_holds
            SET status = $2, finalized_at = now()
            WHERE hold_id = $1 AND status = 'active'
            """;
        command.Parameters.AddWithValue(holdId);
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task FinalizeHoldByLeaseAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string leaseToken, string status, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE balance_holds
            SET status = $2, finalized_at = now()
            WHERE lease_token = $1 AND status = 'active'
            """;
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(status);
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
            WHERE lease_token = $1 AND status = 'active'
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
