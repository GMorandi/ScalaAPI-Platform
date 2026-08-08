using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using ScalaAPI.Grains.Interfaces;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Capnp;
using CapnpGen;

namespace ScalaAPI.Host.Services;

public class CapnpRpcHostedService : IHostedService
{
    private const decimal WireDecimalScale = 100_000_000m;

    private readonly ILogger<CapnpRpcHostedService> _logger;
    private readonly IServiceProvider _services;
    private readonly string _socketPath;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public bool IsListening => _listener is not null;

    public CapnpRpcHostedService(
        ILogger<CapnpRpcHostedService> logger,
        IServiceProvider services,
        IConfiguration configuration)
    {
        _logger = logger;
        _services = services;
        _socketPath = configuration["CapnpRpc:SocketPath"] ?? "/var/run/scalaapi/dispatch.sock";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(128);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_cts.Token);

        _logger.LogInformation("Dispatch RPC server listening on {Path} (capnp binary)", _socketPath);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _listener = null;
        if (_acceptLoop is not null)
            await _acceptLoop.WaitAsync(cancellationToken);
        _logger.LogInformation("Dispatch RPC server stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accept error");
            }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        using var stream = new NetworkStream(client, ownsSocket: true);
        _logger.LogInformation("Gateway connected");

        var hdrBuf = new byte[4];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, hdrBuf, 4, ct)) break;
                var len = BinaryPrimitives.ReadUInt32LittleEndian(hdrBuf);
                if (len == 0 || len > 1024 * 1024) break;

                var payload = new byte[len];
                if (!await ReadExactAsync(stream, payload, (int)len, ct)) break;

                var method = payload[0];
                var capnpData = new ReadOnlyMemory<byte>(payload, 1, payload.Length - 1);

                var response = await ProcessMessageAsync(method, capnpData);
                var respHdr = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(respHdr, (uint)response.Length);
                await stream.WriteAsync(respHdr, ct);
                await stream.WriteAsync(response, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Client disconnected");
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buf, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }

    private async Task<byte[]> ProcessMessageAsync(byte method, ReadOnlyMemory<byte> capnpData)
    {
        var dispatchService = _services.GetRequiredService<DispatchService>();

        try
        {
            return method switch
            {
                1 => await HandleDispatchAsync(dispatchService, capnpData),
                2 => await HandleReportUsageAsync(dispatchService, capnpData),
                3 => await HandleAbortAsync(dispatchService, capnpData),
                4 => await HandleUpstreamErrorAsync(dispatchService, capnpData),
                5 => await HandleMediaOperationAsync(dispatchService, capnpData),
                _ => SerializeRejectResponse("unknown method"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RPC processing error");
            return method is 2 or 3 or 4
                ? SerializeWriteAck((byte)(0x80 + method), WriteAck.Error("platform_error", retryable: true))
                : method is 5
                    ? SerializeMediaOperationResponse(MediaOperationRpcResult.Error(
                        500, "platform_error", "Platform media operation failed"))
                : SerializeRejectResponse("Platform dispatch failed");
        }
    }

    private static DeserializerState DeserializeRoot(ReadOnlyMemory<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var reader = new BinaryReader(ms);
        var frame = Framing.ReadWireFrame(reader);
        return DeserializerState.CreateRoot(frame);
    }

    private static byte[] SerializeToFrame(byte responseMethod, WireFrame frame)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(responseMethod);
        var pump = new FramePump(ms);
        pump.Send(frame);
        pump.Flush();
        return ms.ToArray();
    }

    private static async Task<byte[]> HandleDispatchAsync(DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var capnpReq = CapnpSerializable.Create<CapnpGen.DispatchRequest>(state)
            ?? throw new InvalidDataException("Missing dispatch request root");
        if (capnpReq.ProtocolVersion != 2)
            return BuildDispatchResponse(DispatchResult.Rejected(
                "invalidKey", "Unsupported dispatch protocol version"));

        var req = new DispatchRequest(
            ApiKeyHash: capnpReq.ApiKeyHash ?? "",
            RequestedModel: capnpReq.RequestedModel ?? "",
            SessionHash: capnpReq.SessionHash ?? "",
            ClientIp: capnpReq.ClientIp ?? "",
            RequestId: capnpReq.RequestId ?? "",
            ExcludedAccountIds: capnpReq.ExcludedAccounts?.ToArray() ?? [],
            CachedAuthVersion: capnpReq.CachedAuthVersion,
            Endpoint: capnpReq.Endpoint switch
            {
                CapnpGen.DispatchRequest.EndpointKind.messages => "messages",
                CapnpGen.DispatchRequest.EndpointKind.chatCompletions => "chat_completions",
                CapnpGen.DispatchRequest.EndpointKind.responses => "responses",
                CapnpGen.DispatchRequest.EndpointKind.gemini => "gemini",
                CapnpGen.DispatchRequest.EndpointKind.embeddings => "embeddings",
                CapnpGen.DispatchRequest.EndpointKind.images => "images",
                CapnpGen.DispatchRequest.EndpointKind.videos => "videos",
                CapnpGen.DispatchRequest.EndpointKind.countTokens => "count_tokens",
                CapnpGen.DispatchRequest.EndpointKind.models => "models",
                CapnpGen.DispatchRequest.EndpointKind.alphaSearch => "search",
                CapnpGen.DispatchRequest.EndpointKind.realtime => "realtime",
                CapnpGen.DispatchRequest.EndpointKind.antigravity => "antigravity",
                _ => "unknown",
            },
            MetadataUserId: capnpReq.MetadataUserId,
            Stream: capnpReq.Stream,
            Operation: capnpReq.Operation ?? "",
            InboundFormat: capnpReq.InboundFormat ?? "",
            HttpMethod: capnpReq.HttpMethod ?? "POST",
            RequestPath: capnpReq.RequestPath ?? "",
            ContentType: capnpReq.ContentType ?? "",
            Capability: capnpReq.Capability ?? "",
            IdempotencyKey: capnpReq.IdempotencyKey ?? "",
            RealtimeSession: capnpReq.RealtimeSession,
            ForcePlatform: capnpReq.ForcePlatform ?? "",
            RequestFingerprint: capnpReq.RequestFingerprint ?? "",
            RequestQuery: capnpReq.RequestQuery ?? "");

        var result = (await svc.HandleDispatch(req)) with
        {
            ProtocolVersion = capnpReq.ProtocolVersion
        };
        return BuildDispatchResponse(result);
    }

    private static byte[] BuildDispatchResponse(DispatchResult result)
    {
        var message = MessageBuilder.Create();
        var writer = message.BuildRoot<CapnpGen.DispatchResponse.WRITER>();

        writer.TheOutcome = result.Outcome switch
        {
            "ok" => CapnpGen.DispatchResponse.Outcome.ok,
            "wait" => CapnpGen.DispatchResponse.Outcome.wait,
            "reauth" => CapnpGen.DispatchResponse.Outcome.reauth,
            _ => CapnpGen.DispatchResponse.Outcome.rejected,
        };

        writer.AuthVersion = result.AuthVersion;
        writer.LeaseToken = result.Upstream?.LeaseToken ?? "";

        if (result.Outcome == "rejected")
        {
            var reject = writer.Reject;
            reject.Message = result.RejectMessage ?? "";
            reject.Code = MapRejectCode(result.RejectCode);
        }
        else if (result.Outcome == "wait")
        {
            var waitPlan = writer.WaitPlan;
            waitPlan.TimeoutMs = result.WaitTimeoutMs;
        }
        else if (result.Outcome == "ok" && result.Upstream is not null)
        {
            var auth = writer.Auth;
            auth.ApiKeyId = result.Upstream.ApiKeyId;
            auth.UserId = result.Upstream.UserId;
            auth.GroupId = result.Upstream.GroupId;
            auth.Version = result.Upstream.AuthVersion;

            var up = writer.Upstream;
            up.AccountId = result.Upstream.AccountId;
            up.Platform = result.Upstream.Platform;
            up.BaseUrl = result.Upstream.BaseUrl;
            up.UpstreamPath = result.Upstream.UpstreamPath;
            up.MappedModel = result.Upstream.MappedModel;
            up.UserId = result.Upstream.UserId;
            up.GroupId = result.Upstream.GroupId;
            up.TlsFingerprint = result.Upstream.TlsFingerprint;
            up.HttpMethod = result.Upstream.HttpMethod;
            up.UpstreamFormat = result.Upstream.UpstreamFormat;
            up.WebsocketUrl = result.Upstream.WebsocketUrl;
            up.WebsocketProtocol = result.Upstream.WebsocketProtocol;
            up.TlsFingerprintProfileId = result.Upstream.TlsFingerprintProfileId;
            up.MediaOperationId = result.Upstream.MediaOperationId;
            up.UpstreamTaskId = result.Upstream.UpstreamTaskId;
            up.PollingSupported = result.Upstream.PollingSupported;
            up.ContentDownloadSupported = result.Upstream.ContentDownloadSupported;

            if (!string.IsNullOrEmpty(result.Upstream.ProxyUrl))
            {
                var proxy = up.Proxy;
                proxy.Enabled = true;
                proxy.Url = result.Upstream.ProxyUrl;
            }

            var billing = up.Billing;
            billing.RateMultiplier = ToWireDecimal(result.Upstream.RateMultiplier);
            billing.HoldAmount = ToWireDecimal(result.Upstream.HoldAmount);
            billing.HoldHandle = result.Upstream.HoldHandle ?? "";

            if (result.Upstream.AuthHeaders.Count > 0)
            {
                var headers = result.Upstream.AuthHeaders
                    .Select(kv => new CapnpGen.UpstreamTarget.Header { Key = kv.Key, Value = kv.Value })
                    .ToList();
                up.AuthHeaders.Init(headers, (w, v) => { w.Key = v.Key; w.Value = v.Value; });
            }
            if (result.Upstream.RequestHeaders.Count > 0)
            {
                var headers = result.Upstream.RequestHeaders
                    .Select(kv => new CapnpGen.UpstreamTarget.Header { Key = kv.Key, Value = kv.Value })
                    .ToList();
                up.RequestHeaders.Init(headers, (w, v) => { w.Key = v.Key; w.Value = v.Value; });
            }
            up.AllowedResponseHeaders.Init(result.Upstream.AllowedResponseHeaders);
            up.CapabilityFlags.Init(result.Upstream.CapabilityFlags);
        }

        writer.ProtocolVersion = result.ProtocolVersion;
        return SerializeToFrame(0x81, message.Frame);
    }

    private static long ToWireDecimal(decimal value) =>
        checked(decimal.ToInt64(decimal.Round(value * WireDecimalScale, 0,
            MidpointRounding.AwayFromZero)));

    private static CapnpGen.RejectInfo.RejectCode MapRejectCode(string? code) => code switch
    {
        "invalidKey" => CapnpGen.RejectInfo.RejectCode.invalidKey,
        "expired" => CapnpGen.RejectInfo.RejectCode.expired,
        "noBalance" => CapnpGen.RejectInfo.RejectCode.noBalance,
        "rateLimited" or "rpmExceeded" => CapnpGen.RejectInfo.RejectCode.rateLimited,
        "noAccount" => CapnpGen.RejectInfo.RejectCode.noAccount,
        "concurrencyExceeded" => CapnpGen.RejectInfo.RejectCode.concurrencyExceeded,
        "ipBlocked" => CapnpGen.RejectInfo.RejectCode.ipBlocked,
        "quotaExhausted" => CapnpGen.RejectInfo.RejectCode.quotaExhausted,
        "idempotencyConflict" => CapnpGen.RejectInfo.RejectCode.idempotencyConflict,
        "unsupportedCapability" => CapnpGen.RejectInfo.RejectCode.unsupportedCapability,
        "idempotencyReplay" => CapnpGen.RejectInfo.RejectCode.idempotencyReplay,
        "pricingUnavailable" => CapnpGen.RejectInfo.RejectCode.pricingUnavailable,
        _ => CapnpGen.RejectInfo.RejectCode.invalidKey,
    };

    private static async Task<byte[]> HandleReportUsageAsync(DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var report = CapnpSerializable.Create<CapnpGen.UsageReport>(state)
            ?? throw new InvalidDataException("Missing usage report root");

        var req = new UsageReportRequest(
            LeaseToken: report.LeaseToken ?? "",
            InputTokens: report.InputTokens,
            OutputTokens: report.OutputTokens,
            CacheCreateTokens: report.CacheCreateTokens,
            CacheReadTokens: report.CacheReadTokens,
            DurationMs: report.DurationMs,
            FirstTokenMs: report.FirstTokenMs,
            StatusCode: report.StatusCode,
            Stream: report.Stream,
            ClientDisconnect: report.ClientDisconnect,
            InputImageCount: report.InputImageCount,
            OutputImageCount: report.OutputImageCount,
            ImageSize: report.ImageSize ?? "",
            VideoCount: report.VideoCount,
            VideoResolution: report.VideoResolution ?? "",
            VideoDurationSeconds: report.VideoDurationSeconds,
            RealtimeDurationMs: report.RealtimeDurationMs,
            RealtimeFrames: report.RealtimeFrames,
            DisconnectReason: report.DisconnectReason ?? "",
            ProviderUsageJson: report.ProviderUsageJson ?? "",
            ReasoningTokens: report.ReasoningTokens,
            ServiceTier: report.ServiceTier ?? "",
            UpstreamEndpoint: report.UpstreamEndpoint ?? "",
            CancellationReason: report.CancellationReason ?? "",
            MediaOperationId: report.MediaOperationId ?? "",
            PricingVersion: report.PricingVersion ?? "");

        var ack = await svc.HandleReportUsage(req);
        return SerializeWriteAck(0x82, ack);
    }

    private static async Task<byte[]> HandleAbortAsync(DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var reader = CapnpGen.AbortRequest.READER.create(state);
        var leaseToken = reader.LeaseToken ?? "";
        var reason = reader.Reason ?? "";

        var ack = await svc.HandleAbort(leaseToken, reason);
        return SerializeWriteAck(0x83, ack);
    }

    private static async Task<byte[]> HandleUpstreamErrorAsync(DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var report = CapnpSerializable.Create<CapnpGen.ErrorReport>(state)
            ?? throw new InvalidDataException("Missing error report root");

        await svc.HandleUpstreamError(report.AccountId, report.StatusCode,
            report.RetryAfterMs > 0 ? report.RetryAfterMs : null);
        return SerializeWriteAck(0x84, WriteAck.Ok());
    }

    private static async Task<byte[]> HandleMediaOperationAsync(DispatchService svc,
        ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var request = CapnpSerializable.Create<CapnpGen.MediaOperationRequest>(state)
            ?? throw new InvalidDataException("Missing media operation request root");
        var result = await svc.HandleMediaOperation(new MediaOperationRpcRequest(
            request.ApiKeyHash ?? "", request.OperationId ?? "", request.Action ?? "get",
            request.RequestId ?? "", request.ClientIp ?? "", request.IdempotencyKey ?? "",
            request.RequestFingerprint ?? "", request.Status ?? "", request.UpstreamTaskId ?? "",
            request.OutputMetadata ?? "", request.OutputUrl ?? "", request.ContentType ?? "",
            request.Progress));
        return SerializeMediaOperationResponse(result);
    }

    private static byte[] SerializeMediaOperationResponse(MediaOperationRpcResult result)
    {
        var message = MessageBuilder.Create();
        var writer = message.BuildRoot<CapnpGen.MediaOperationResponse.WRITER>();
        writer.Accepted = result.Accepted;
        writer.StatusCode = result.StatusCode;
        writer.OperationId = result.OperationId;
        writer.OperationType = result.OperationType;
        writer.Status = result.Status;
        writer.Progress = result.Progress;
        writer.UpstreamTaskId = result.UpstreamTaskId;
        writer.OutputMetadata = result.OutputMetadata;
        writer.OutputUrl = result.OutputUrl;
        writer.ContentType = result.ContentType;
        writer.ErrorCode = result.ErrorCode;
        writer.ErrorMessage = result.ErrorMessage;
        return SerializeToFrame(0x85, message.Frame);
    }

    private static byte[] SerializeEmptyResponse(byte method)
    {
        return [method];
    }

    private static byte[] SerializeWriteAck(byte method, WriteAck ack)
    {
        var message = MessageBuilder.Create();
        var writer = message.BuildRoot<CapnpGen.WriteAck.WRITER>();
        writer.Accepted = ack.Accepted;
        writer.Duplicate = ack.Duplicate;
        writer.Retryable = ack.Retryable;
        writer.ErrorCode = ack.ErrorCode;
        return SerializeToFrame(method, message.Frame);
    }

    private static byte[] SerializeRejectResponse(string message)
    {
        var response = MessageBuilder.Create();
        var writer = response.BuildRoot<CapnpGen.DispatchResponse.WRITER>();
        writer.TheOutcome = CapnpGen.DispatchResponse.Outcome.rejected;
        writer.Reject.Message = message;
        writer.Reject.Code = CapnpGen.RejectInfo.RejectCode.invalidKey;
        writer.ProtocolVersion = 2;
        return SerializeToFrame(0x81, response.Frame);
    }
}

public class DispatchService
{
    private readonly IClusterClient _cluster;
    private readonly RequestLeaseStore _leases;
    private readonly MediaOperationStore _mediaOperations;
    private readonly ModelPricingService _pricing;
    private readonly AuthProjectionCache _authCache;
    private readonly GarnetWriteThroughService _garnet;
    private readonly ILogger<DispatchService> _logger;
    private readonly TimeSpan _leaseTtl;
    private readonly decimal _maxReservationUsd;

    public DispatchService(IClusterClient cluster, RequestLeaseStore leases,
                           MediaOperationStore mediaOperations,
                           ModelPricingService pricing,
                           AuthProjectionCache authCache, GarnetWriteThroughService garnet,
                           IConfiguration configuration,
                           ILogger<DispatchService> logger)
    {
        _cluster = cluster;
        _leases = leases;
        _mediaOperations = mediaOperations;
        _pricing = pricing;
        _authCache = authCache;
        _garnet = garnet;
        _logger = logger;
        _leaseTtl = TimeSpan.FromSeconds(
            configuration.GetValue("Dispatch:LeaseTtlSeconds", 360));
        _maxReservationUsd = Math.Max(0.01m,
            configuration.GetValue("Dispatch:MaxReservationUsd", 10m));
    }

    public async Task<DispatchResult> HandleDispatch(DispatchRequest req)
    {
        var apiKeyGrain = _cluster.GetGrain<IApiKeyGrain>(req.ApiKeyHash);

        AuthResult auth;
        try
        {
            if (!_authCache.TryGet(req.ApiKeyHash, req.ClientIp, req.CachedAuthVersion, out auth))
            {
                auth = await apiKeyGrain.Validate(new AuthRequest(req.ClientIp, req.RequestId));
                _authCache.Set(req.ApiKeyHash, req.ClientIp, auth);
                _garnet.WriteAuthSnapshot(req.ApiKeyHash, JsonSerializer.Serialize(new
                {
                    version = auth.Version,
                    api_key_id = auth.ApiKeyId,
                    user_id = auth.UserId,
                    group_id = auth.GroupId,
                    status = auth.Status,
                    rate_multiplier = (double)auth.RateMultiplier,
                    rpm_limit = auth.RpmLimit,
                }));
            }
        }
        catch (Exception ex)
        {
            return DispatchResult.Rejected("invalidKey", ex.Message);
        }

        var isMediaOperation = req.Operation is "images_generations_async" or "images_edits_async"
            or "images_batch_create" or "videos_generations" or "videos_edits"
            or "videos_extensions";
        if (!isMediaOperation && !string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            var idempotency = await _leases.CheckIdempotencyAsync(
                auth.ApiKeyId, req.IdempotencyKey, req.RequestFingerprint);
            if (idempotency.Conflict)
                return DispatchResult.Rejected("idempotencyConflict",
                    "Idempotency key was already used for a different request");
            if (idempotency.Found)
                return DispatchResult.Rejected("idempotencyReplay",
                    "Request has already been dispatched");
        }

        if (!string.IsNullOrWhiteSpace(req.RequestedModel)
            && !_pricing.TryGetPrice(req.RequestedModel, out _))
        {
            return DispatchResult.Rejected("pricingUnavailable",
                $"Pricing is not configured for model '{req.RequestedModel}'");
        }

        var userGrain = _cluster.GetGrain<IUserGrain>(auth.UserId);
        if (!await userGrain.CheckBalance(0.01m))
        {
            return DispatchResult.Rejected("noBalance", "Insufficient balance");
        }

        var groupGrain = _cluster.GetGrain<IGroupGrain>(auth.GroupId);
        var groupProj = await groupGrain.GetAuthProjection();
        if (!await groupGrain.CheckAndRecordRpm())
            return DispatchResult.Rejected("rpmExceeded", "Group RPM limit reached");

        if (groupProj.DailyLimitUsd.HasValue)
        {
            var dailySpend = await groupGrain.GetDailySpend();
            if (dailySpend >= groupProj.DailyLimitUsd.Value)
            {
                return DispatchResult.Rejected("quotaExhausted", "Group daily limit reached");
            }
        }

        var schedulerGrain = _cluster.GetGrain<ISchedulerGrain>(auth.GroupId);
        var capability = string.IsNullOrWhiteSpace(req.Capability) ? req.Endpoint : req.Capability;
        if (!string.IsNullOrWhiteSpace(req.ForcePlatform)
            && !string.Equals(groupProj.Platform, "composite", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(groupProj.Platform, req.ForcePlatform, StringComparison.OrdinalIgnoreCase))
        {
            return DispatchResult.Rejected("noAccount", "Requested provider is not enabled for this group");
        }
        var selectedGroupId = auth.GroupId;
        var selectedRateMultiplier = auth.RateMultiplier;
        var selection = await schedulerGrain.Select(new SelectRequest(
            req.RequestedModel, req.SessionHash, req.RequestId,
            req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint,
            capability, req.ForcePlatform));

        if (selection.Outcome == SelectionOutcome.Rejected && groupProj.FallbackGroupId.HasValue)
        {
            selectedGroupId = groupProj.FallbackGroupId.Value;
            var fallbackGroup = _cluster.GetGrain<IGroupGrain>(selectedGroupId);
            var fallbackProjection = await fallbackGroup.GetAuthProjection();
            if (!string.Equals(fallbackProjection.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return DispatchResult.Rejected("noAccount", "Fallback group is disabled");
            }
            if (fallbackProjection.DailyLimitUsd.HasValue &&
                await fallbackGroup.GetDailySpend() >= fallbackProjection.DailyLimitUsd.Value)
            {
                return DispatchResult.Rejected("quotaExhausted", "Fallback group daily limit reached");
            }
            if (!await fallbackGroup.CheckAndRecordRpm())
                return DispatchResult.Rejected("rpmExceeded", "Fallback group RPM limit reached");
            selectedRateMultiplier = await fallbackGroup.GetEffectiveMultiplier(DateTimeOffset.UtcNow);
            var fallbackScheduler = _cluster.GetGrain<ISchedulerGrain>(selectedGroupId);
            selection = await fallbackScheduler.Select(new SelectRequest(
                req.RequestedModel, req.SessionHash, req.RequestId,
                req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint,
                capability, req.ForcePlatform));
        }

        if (selection.Outcome == SelectionOutcome.Rejected)
        {
            return DispatchResult.Rejected("noAccount", selection.RejectReason ?? "No accounts");
        }

        if (selection.Outcome == SelectionOutcome.Wait)
        {
            return DispatchResult.Wait(selection.WaitTimeoutMs ?? 45_000);
        }

        var accountId = selection.AccountId!.Value;
        var accountGrain = _cluster.GetGrain<IAccountGrain>(accountId);
        var userSlot = await userGrain.TryAcquireSlot(selection.LeaseToken!, DateTime.UtcNow.Add(_leaseTtl));
        if (!userSlot.Acquired)
        {
            await accountGrain.ReleaseSlot(selection.LeaseToken!);
            return DispatchResult.Rejected("concurrencyExceeded", "User concurrency limit reached");
        }
        if (auth.RpmLimit > 0 && !await userGrain.CheckAndRecordRpm(auth.RpmLimit))
        {
            await accountGrain.ReleaseSlot(selection.LeaseToken!);
            await userGrain.ReleaseSlot(selection.LeaseToken!);
            return DispatchResult.Rejected("rpmExceeded", "User RPM limit reached");
        }
        HoldHandle? hold = null;
        try
        {
            var creds = await accountGrain.Hydrate();
            await accountGrain.RecordRpm();

            var holdAmount = _maxReservationUsd *
                Math.Max(1m, selectedRateMultiplier);
            hold = await userGrain.ReserveBalance(holdAmount);
            if (hold is null)
            {
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("noBalance", "Insufficient available balance");
            }

            var mappedModel = creds.ModelMapping.GetValueOrDefault(
                req.RequestedModel, req.RequestedModel);
            var upstreamPath = ResolveUpstreamPath(
                creds.Platform, req, mappedModel);
            var created = await _leases.CreateDetailedAsync(new LeaseCreateRequest(
                selection.LeaseToken!, req.RequestId, req.ApiKeyHash, auth.ApiKeyId,
                auth.UserId, accountId, selectedGroupId, req.RequestedModel, mappedModel,
                req.Endpoint, selectedRateMultiplier, hold.Id, hold.Amount,
                DateTime.UtcNow.Add(_leaseTtl),
                isMediaOperation ? "" : req.IdempotencyKey,
                isMediaOperation ? "" : req.RequestFingerprint));
            if (created.Conflict)
            {
                await userGrain.ReleaseHold(hold);
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("idempotencyConflict",
                    "Idempotency key was already used for a different request");
            }
            if (!created.Created)
            {
                await userGrain.ReleaseHold(hold);
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("idempotencyReplay",
                    "Request has already been dispatched");
            }

            var mediaOperationId = "";
            if (isMediaOperation)
            {
                var ttl = capability == "images_async" ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24);
                var media = await _mediaOperations.CreateOrGetAsync(
                    auth.ApiKeyId, accountId, req.RequestId, selection.LeaseToken!,
                    req.Operation, req.IdempotencyKey, req.RequestFingerprint,
                    creds.Platform, DateTime.UtcNow.Add(ttl));
                if (media.Conflict)
                {
                    await _leases.AbortAsync(selection.LeaseToken!,
                        "idempotency_conflict");
                    await accountGrain.ReleaseSlot(selection.LeaseToken!);
                    await userGrain.ReleaseSlot(selection.LeaseToken!);
                    return DispatchResult.Rejected("idempotencyConflict",
                        "Idempotency key was already used for a different request");
                }
                if (!media.Created)
                {
                    await _leases.AbortAsync(selection.LeaseToken!, "idempotency_replay");
                    await accountGrain.ReleaseSlot(selection.LeaseToken!);
                    await userGrain.ReleaseSlot(selection.LeaseToken!);
                    return DispatchResult.Rejected("idempotencyReplay",
                        $"Media operation already exists: {media.Operation.OperationId}");
                }
                mediaOperationId = media.Operation.OperationId;
            }

            return DispatchResult.Ok(new UpstreamTargetResult
            {
                AccountId = accountId,
                Platform = creds.Platform,
                BaseUrl = creds.BaseUrl,
                UpstreamPath = upstreamPath,
                AuthHeaders = creds.AuthHeaders,
                MappedModel = mappedModel,
                ProxyUrl = creds.ProxyUrl,
                TlsFingerprint = creds.TlsFingerprint,
                ApiKeyId = auth.ApiKeyId,
                UserId = auth.UserId,
                GroupId = selectedGroupId,
                RateMultiplier = selectedRateMultiplier,
                HoldAmount = hold.Amount,
                HoldHandle = hold.Id,
                LeaseToken = selection.LeaseToken!,
                AuthVersion = auth.Version,
                HttpMethod = ResolveUpstreamMethod(req),
                UpstreamFormat = ResolveUpstreamFormat(creds.Platform, req),
                AllowedResponseHeaders = [
                    "Content-Type", "Retry-After", "X-Request-ID", "OpenAI-Request-ID",
                    "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset"],
                WebsocketUrl = req.RealtimeSession ? ToWebsocketUrl(creds.BaseUrl, upstreamPath) : "",
                CapabilityFlags = [capability],
                MediaOperationId = mediaOperationId,
                PollingSupported = req.Operation.Contains("get", StringComparison.OrdinalIgnoreCase)
                    || capability is "images_async" or "videos",
                ContentDownloadSupported = capability is "images_async" or "images_batch" or "videos",
            });
        }
        catch (Exception ex)
        {
            if (hold is not null)
                await userGrain.ReleaseHold(hold);
            await accountGrain.ReleaseSlot(selection.LeaseToken!);
            await userGrain.ReleaseSlot(selection.LeaseToken!);
            _logger.LogError(ex,
                "Dispatch compensation for request {RequestId}, account {AccountId}",
                req.RequestId, accountId);
            return DispatchResult.Rejected("noAccount", "Unable to create request lease");
        }
    }

    public async Task<WriteAck> HandleReportUsage(UsageReportRequest req)
    {
        return await _leases.CompleteAsync(new LeaseCompletion(
            req.LeaseToken,
            req.InputTokens, req.OutputTokens, req.CacheCreateTokens,
            req.CacheReadTokens, req.DurationMs, req.FirstTokenMs,
            req.StatusCode, req.Stream, req.ClientDisconnect,
            req.InputImageCount, req.OutputImageCount, req.ImageSize,
            req.VideoCount, req.VideoResolution, req.VideoDurationSeconds,
            req.RealtimeDurationMs, req.RealtimeFrames, req.DisconnectReason,
            req.ProviderUsageJson, req.ReasoningTokens, req.ServiceTier,
            req.UpstreamEndpoint, req.CancellationReason,
            req.MediaOperationId, req.PricingVersion));
    }

    public async Task<MediaOperationRpcResult> HandleMediaOperation(
        MediaOperationRpcRequest req)
    {
        AuthResult auth;
        try
        {
            auth = await _cluster.GetGrain<IApiKeyGrain>(req.ApiKeyHash)
                .Validate(new AuthRequest(req.ClientIp, req.RequestId));
        }
        catch
        {
            return MediaOperationRpcResult.Error(401, "authentication_error", "Invalid API key");
        }

        MediaOperation? operation;
        if (req.Action == "lookup_idempotency")
        {
            operation = await _mediaOperations.GetByIdempotencyAsync(
                auth.ApiKeyId, req.IdempotencyKey);
            if (operation is null)
                return MediaOperationRpcResult.Error(404, "not_found_error", "Media operation not found");
            if (!string.Equals(operation.RequestFingerprint, req.RequestFingerprint,
                    StringComparison.Ordinal))
                return MediaOperationRpcResult.Error(409, "idempotency_conflict",
                    "Idempotency key was already used for a different request");
            return MediaOperationRpcResult.From(operation);
        }

        if (string.IsNullOrWhiteSpace(req.OperationId))
            return MediaOperationRpcResult.Error(400, "invalid_request_error", "Operation ID is required");

        switch (req.Action)
        {
            case "cancel":
                operation = await _mediaOperations.CancelAsync(auth.ApiKeyId, req.OperationId);
                if (operation is not null)
                    await _leases.AbortAsync(operation.LeaseToken, "media_operation_canceled");
                break;
            case "attach":
            case "complete":
            case "fail":
                operation = await _mediaOperations.UpdateAsync(auth.ApiKeyId, req.OperationId,
                    string.IsNullOrWhiteSpace(req.Status) ? "running" : req.Status,
                    req.Progress, req.UpstreamTaskId, req.OutputMetadata,
                    req.OutputUrl, req.ContentType,
                    req.Action == "fail" ? req.OutputMetadata : null);
                break;
            case "delete":
                return await _mediaOperations.DeleteAsync(auth.ApiKeyId, req.OperationId)
                    ? new MediaOperationRpcResult(true, 204, req.OperationId, "", "", 100,
                        "", "", "", "", "", "")
                    : MediaOperationRpcResult.Error(409, "operation_not_terminal",
                        "Only terminal media operations can be deleted");
            case "delete_outputs":
                operation = await _mediaOperations.ClearOutputsAsync(auth.ApiKeyId, req.OperationId);
                break;
            default:
                operation = await _mediaOperations.GetAsync(auth.ApiKeyId, req.OperationId);
                break;
        }

        if (operation is null)
            return MediaOperationRpcResult.Error(404, "not_found_error", "Media operation not found");
        if (req.Action is "content" or "download"
            && (operation.Status != "succeeded" || string.IsNullOrWhiteSpace(operation.OutputUrl)))
            return MediaOperationRpcResult.Error(409, "output_not_ready", "Media output is not ready");
        return MediaOperationRpcResult.From(operation);
    }

    public async Task<WriteAck> HandleAbort(string leaseToken, string reason)
    {
        return await _leases.AbortAsync(leaseToken, reason);
    }

    public async Task HandleUpstreamError(long accountId, int statusCode, int? retryAfterMs)
    {
        var accountGrain = _cluster.GetGrain<IAccountGrain>(accountId);
        await accountGrain.ReportUpstreamError(new ErrorInfo(statusCode, retryAfterMs, null));
    }

    private static string ResolveUpstreamPath(string platform, DispatchRequest req,
        string mappedModel)
    {
        string path;
        if (req.Capability == "images_sync")
            path = req.Operation == "images_edits" ? "/v1/images/edits" : "/v1/images/generations";
        else if (req.Capability == "images_async")
            path = req.Operation == "images_task_get" ? SafePathOrDefault(req.RequestPath, "/v1/images/tasks")
                : req.Operation == "images_edits_async" ? "/v1/images/edits/async"
                : "/v1/images/generations/async";
        else if (req.Capability == "images_batch")
            path = SafePathWithPrefix(req.RequestPath, "/v1/images/batches", "/v1/images/batches");
        else if (req.Capability == "videos")
            path = req.Operation switch
            {
                "videos_generations" => "/v1/videos/generations",
                "videos_edits" => "/v1/videos/edits",
                "videos_extensions" => "/v1/videos/extensions",
                _ => SafePathWithPrefix(req.RequestPath, "/v1/videos/", "/v1/videos/generations")
            };
        else if (req.Capability == "search") path = "/alpha/search";
        else if (req.Capability == "embeddings") path = "/v1/embeddings";
        else if (req.Capability == "models") path = "/v1/models";
        else if (req.Operation == "antigravity_models") path = "/v1/models";
        else if (req.Operation == "antigravity_usage") path = "/v1/usage";
        else if (req.Capability == "gemini_models")
            path = req.Operation == "gemini_models_get"
                ? $"/v1beta/models/{Uri.EscapeDataString(mappedModel)}" : "/v1beta/models";
        else if (req.Capability == "count_tokens") path = "/v1/messages/count_tokens";
        else if (req.Capability == "realtime")
            path = req.Operation switch
            {
                "codex_realtime_calls" => "/backend-api/codex/realtime/calls",
                "codex_live_sideband" => SafePathWithPrefix(req.RequestPath,
                    "/backend-api/codex/", "/backend-api/codex"),
                "live_create" => "/v1/live",
                "live_sideband" => SafePathWithPrefix(req.RequestPath, "/v1/live/", "/v1/live"),
                _ => "/v1/responses"
            };
        else if (platform is "anthropic" or "claude") path = "/v1/messages";
        else if (platform is "gemini" or "google")
            path = req.Stream
                ? $"/v1beta/models/{Uri.EscapeDataString(mappedModel)}:streamGenerateContent?alt=sse"
                : $"/v1beta/models/{Uri.EscapeDataString(mappedModel)}:generateContent";
        else if (req.Capability is "responses" or "responses_subpath")
            path = "/v1/responses" + ResponsesSuffix(req.RequestPath);
        else path = "/v1/chat/completions";

        return AppendSafeQuery(path, req.RequestQuery);
    }

    private static string ResolveUpstreamMethod(DispatchRequest req) =>
        req.Operation is "models" or "gemini_models_list" or "gemini_models_get"
            or "antigravity_models" or "antigravity_usage"
            or "images_task_get" or "videos_get" ? "GET" : req.HttpMethod switch
            { "GET" or "PUT" or "PATCH" or "DELETE" => req.HttpMethod, _ => "POST" };

    private static string ResolveUpstreamFormat(string platform, DispatchRequest req) =>
        platform is "anthropic" or "claude" ? "anthropic"
        : platform is "gemini" or "google" ? "gemini"
        : req.Capability is "responses" or "responses_subpath" ? "openai_responses"
        : "openai_chat";

    private static string SafePathOrDefault(string path, string fallback)
    {
        var marker = path.LastIndexOf('/');
        if (marker < 0) return fallback;
        var suffix = path[marker..];
        return suffix.Length > 1 && suffix.All(c => char.IsLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
            ? fallback + suffix : fallback;
    }

    private static string SafePathWithPrefix(string path, string prefix, string fallback)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return fallback;
        return path.All(c => char.IsLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
            ? path : fallback;
    }

    private static string ResponsesSuffix(string path)
    {
        var marker = path.LastIndexOf("/responses", StringComparison.Ordinal);
        if (marker < 0) return "";
        var suffix = path[(marker + "/responses".Length)..];
        if (string.IsNullOrEmpty(suffix) || suffix == "/") return "";
        var valid = suffix.Split('/').Skip(1).All(segment => !string.IsNullOrEmpty(segment)
            && segment.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        return valid ? suffix : "";
    }

    private static string AppendSafeQuery(string path, string query)
    {
        if (string.IsNullOrEmpty(query)) return path;
        if (query.Length > 4096 || query[0] != '?' || query.Contains('#')) return path;
        for (var i = 1; i < query.Length; i++)
        {
            var c = query[i];
            if (char.IsControl(c) || c > 0x7f) return path;
            if (c != '%') continue;
            if (i + 2 >= query.Length || !Uri.IsHexDigit(query[i + 1])
                || !Uri.IsHexDigit(query[i + 2])) return path;
            i += 2;
        }
        return path.Contains('?') ? path + "&" + query[1..] : path + query;
    }

    private static string ToWebsocketUrl(string baseUrl, string path)
    {
        var scheme = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://"
            : baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" : "";
        return string.IsNullOrEmpty(scheme) ? "" : scheme + baseUrl[(baseUrl.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/') + path;
    }
}

public record DispatchRequest(
    string ApiKeyHash, string RequestedModel, string SessionHash,
    string ClientIp, string RequestId, long[] ExcludedAccountIds,
    long CachedAuthVersion, string Endpoint, string? MetadataUserId, bool Stream,
    string Operation = "", string InboundFormat = "", string HttpMethod = "POST",
    string RequestPath = "", string ContentType = "", string Capability = "",
    string IdempotencyKey = "", bool RealtimeSession = false, string ForcePlatform = "",
    string RequestFingerprint = "", string RequestQuery = "");

public record UsageReportRequest(
    string LeaseToken, int InputTokens, int OutputTokens,
    int CacheCreateTokens, int CacheReadTokens, int DurationMs,
    int FirstTokenMs, int StatusCode, bool Stream, bool ClientDisconnect,
    int InputImageCount = 0, int OutputImageCount = 0, string ImageSize = "",
    int VideoCount = 0, string VideoResolution = "", int VideoDurationSeconds = 0,
    int RealtimeDurationMs = 0, int RealtimeFrames = 0, string DisconnectReason = "",
    string ProviderUsageJson = "", int ReasoningTokens = 0, string ServiceTier = "",
    string UpstreamEndpoint = "", string CancellationReason = "",
    string MediaOperationId = "", string PricingVersion = "");

public sealed record MediaOperationRpcRequest(
    string ApiKeyHash, string OperationId, string Action, string RequestId,
    string ClientIp, string IdempotencyKey, string RequestFingerprint,
    string Status, string UpstreamTaskId, string OutputMetadata,
    string OutputUrl, string ContentType, int Progress);

public sealed record MediaOperationRpcResult(
    bool Accepted, int StatusCode, string OperationId, string OperationType,
    string Status, int Progress, string UpstreamTaskId, string OutputMetadata,
    string OutputUrl, string ContentType, string ErrorCode, string ErrorMessage)
{
    public static MediaOperationRpcResult From(MediaOperation operation) => new(
        true, 200, operation.OperationId, operation.OperationType, operation.Status,
        operation.Progress, operation.UpstreamTaskId, operation.OutputMetadata,
        operation.OutputUrl, operation.ContentType, "", "");

    public static MediaOperationRpcResult Error(int statusCode, string code, string message) =>
        new(false, statusCode, "", "", "", 0, "", "", "", "", code, message);
}

public record UpstreamTargetResult
{
    public long AccountId { get; init; }
    public string Platform { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string UpstreamPath { get; init; } = "";
    public Dictionary<string, string> AuthHeaders { get; init; } = new();
    public string MappedModel { get; init; } = "";
    public string? ProxyUrl { get; init; }
    public bool TlsFingerprint { get; init; }
    public long ApiKeyId { get; init; }
    public long UserId { get; init; }
    public long GroupId { get; init; }
    public decimal RateMultiplier { get; init; }
    public decimal HoldAmount { get; init; }
    public string? HoldHandle { get; init; }
    public string LeaseToken { get; init; } = "";
    public long AuthVersion { get; init; }
    public string HttpMethod { get; init; } = "POST";
    public string UpstreamFormat { get; init; } = "";
    public Dictionary<string, string> RequestHeaders { get; init; } = new();
    public string[] AllowedResponseHeaders { get; init; } = [];
    public string WebsocketUrl { get; init; } = "";
    public string WebsocketProtocol { get; init; } = "";
    public string TlsFingerprintProfileId { get; init; } = "";
    public string[] CapabilityFlags { get; init; } = [];
    public string MediaOperationId { get; init; } = "";
    public string UpstreamTaskId { get; init; } = "";
    public bool PollingSupported { get; init; }
    public bool ContentDownloadSupported { get; init; }
}

public record DispatchResult
{
    public string Outcome { get; init; } = "ok";
    public UpstreamTargetResult? Upstream { get; init; }
    public string? RejectCode { get; init; }
    public string? RejectMessage { get; init; }
    public int WaitTimeoutMs { get; init; }
    public long AuthVersion { get; init; }
    public ushort ProtocolVersion { get; init; } = 2;

    public static DispatchResult Ok(UpstreamTargetResult upstream) =>
        new() { Outcome = "ok", Upstream = upstream, AuthVersion = upstream.AuthVersion };

    public static DispatchResult Rejected(string code, string message) =>
        new() { Outcome = "rejected", RejectCode = code, RejectMessage = message };

    public static DispatchResult Wait(int timeoutMs) =>
        new() { Outcome = "wait", WaitTimeoutMs = timeoutMs };
}
