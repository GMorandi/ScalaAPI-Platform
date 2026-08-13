using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using ScalaAPI.Grains.Interfaces;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;
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
                6 => await HandleLeaseEvidenceAsync(dispatchService, capnpData),
                7 => await HandleContentPolicyAsync(dispatchService, capnpData),
                _ => SerializeRejectResponse("unknown method"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RPC processing error");
            return method is 2 or 3 or 4 or 6
                ? SerializeWriteAck((byte)(0x80 + method), WriteAck.Error("platform_error", retryable: true))
                : method is 5
                    ? SerializeMediaOperationResponse(MediaOperationRpcResult.Error(
                        500, "platform_error", "Platform media operation failed"))
                : method is 7
                    ? SerializeContentPolicyResponse(ContentPolicyRpcResult.Error(
                        "platform_error", "Content policy service failed", retryable: true))
                : method == 1
                    ? SerializeRejectResponse("Platform dispatch failed; retry may be safe",
                        CapnpGen.RejectInfo.RejectCode.platformUnavailable)
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
        if (capnpReq.ProtocolVersion != 3)
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
            RequestQuery: capnpReq.RequestQuery ?? "",
            RequestBody: capnpReq.RequestBody ?? "");

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
                proxy.Username = result.Upstream.ProxyUsername ?? "";
                proxy.Password = result.Upstream.ProxyPassword ?? "";
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
        writer.ReplayStatusCode = result.ReplayStatusCode;
        writer.ReplayContentType = result.ReplayContentType;
        writer.ReplayBody = result.ReplayBody;
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
        "platformUnavailable" => CapnpGen.RejectInfo.RejectCode.platformUnavailable,
        "contentPolicyBlocked" => CapnpGen.RejectInfo.RejectCode.contentPolicyBlocked,
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
            PricingVersion: report.PricingVersion ?? "",
            ResponseStatusCode: report.ResponseStatusCode,
            ResponseContentType: report.ResponseContentType ?? "",
            ResponseBody: report.ResponseBody ?? "");

        var ack = await svc.HandleReportUsage(req);
        return SerializeWriteAck(0x82, ack);
    }

    private static async Task<byte[]> HandleAbortAsync(DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var reader = CapnpGen.AbortRequest.READER.create(state);
        var leaseToken = reader.LeaseToken ?? "";
        var reason = reader.Reason ?? "";
        var disposition = reader.TheDisposition == CapnpGen.AbortRequest.Disposition.unknown
            ? LeaseAbortDisposition.Unknown : LeaseAbortDisposition.NoCharge;
        var providerStatusCode = reader.ProviderStatusCode is >= 100 and <= 999
            ? reader.ProviderStatusCode : (int?)null;

        var ack = await svc.HandleAbort(leaseToken, reason, disposition, providerStatusCode);
        return SerializeWriteAck(0x83, ack);
    }

    private static async Task<byte[]> HandleLeaseEvidenceAsync(
        DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var reader = CapnpGen.LeaseEvidence.READER.create(state);
        var stage = reader.TheStage == CapnpGen.LeaseEvidence.Stage.outputStarted
            ? LeaseEvidenceStage.OutputStarted : LeaseEvidenceStage.Forwarded;
        var ack = await svc.HandleLeaseEvidence(reader.LeaseToken ?? "", stage,
            reader.Source ?? "gateway", reader.Detail ?? "");
        return SerializeWriteAck(0x86, ack);
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

    private static async Task<byte[]> HandleContentPolicyAsync(
        DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var request = CapnpSerializable.Create<CapnpGen.ContentPolicyRequest>(state)
            ?? throw new InvalidDataException("Missing content policy request root");
        var stage = request.TheStage == CapnpGen.ContentPolicyRequest.Stage.response
            ? ContentPolicyStage.Response : ContentPolicyStage.Request;
        var result = await svc.HandleContentPolicy(new ContentPolicyRpcRequest(
            request.LeaseToken ?? "", request.Content ?? "",
            request.Capability ?? "", stage));
        return SerializeContentPolicyResponse(result);
    }

    private static byte[] SerializeContentPolicyResponse(ContentPolicyRpcResult result)
    {
        var message = MessageBuilder.Create();
        var writer = message.BuildRoot<CapnpGen.ContentPolicyResponse.WRITER>();
        writer.Evaluated = result.Evaluated;
        writer.Allowed = result.Allowed;
        writer.Retryable = result.Retryable;
        writer.ErrorCode = result.ErrorCode;
        writer.MatchedRuleId = result.MatchedRuleId;
        writer.Message = result.Message;
        return SerializeToFrame(0x87, message.Frame);
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

    private static byte[] SerializeRejectResponse(string message,
        CapnpGen.RejectInfo.RejectCode code = CapnpGen.RejectInfo.RejectCode.invalidKey)
    {
        var response = MessageBuilder.Create();
        var writer = response.BuildRoot<CapnpGen.DispatchResponse.WRITER>();
        writer.TheOutcome = CapnpGen.DispatchResponse.Outcome.rejected;
        writer.Reject.Message = message;
        writer.Reject.Code = code;
        writer.ProtocolVersion = 3;
        return SerializeToFrame(0x81, response.Frame);
    }
}

public class DispatchService
{
    private readonly IClusterClient _cluster;
    private readonly RequestLeaseStore _leases;
    private readonly MediaOperationStore _mediaOperations;
    private readonly ObjectStorageClient _objectStorage;
    private readonly ModelPricingService _pricing;
    private readonly AuthProjectionCache _authCache;
    private readonly ProviderCredentialRefreshService _credentials;
    private readonly ProviderMediaCancellationClient _mediaCancellation;
    private readonly GarnetWriteThroughService _garnet;
    private readonly ILogger<DispatchService> _logger;
    private readonly FaultInjection _faults;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ContentPolicyService _contentPolicy;
    private readonly TimeSpan _leaseTtl;
    private readonly decimal _maxReservationUsd;
    private readonly int _asyncMediaRetentionHours;
    private readonly int _batchMediaRetentionHours;

    public DispatchService(IClusterClient cluster, RequestLeaseStore leases,
                           MediaOperationStore mediaOperations,
                           ObjectStorageClient objectStorage,
                           ModelPricingService pricing,
                           AuthProjectionCache authCache, GarnetWriteThroughService garnet,
                           ProviderCredentialRefreshService credentials,
                           ProviderMediaCancellationClient mediaCancellation,
                           NpgsqlDataSource dataSource,
                           IConfiguration configuration,
                           ILogger<DispatchService> logger,
                           FaultInjection faults,
                           ContentPolicyService contentPolicy)
    {
        _cluster = cluster;
        _leases = leases;
        _mediaOperations = mediaOperations;
        _objectStorage = objectStorage;
        _pricing = pricing;
        _authCache = authCache;
        _credentials = credentials;
        _mediaCancellation = mediaCancellation;
        _dataSource = dataSource;
        _garnet = garnet;
        _logger = logger;
        _faults = faults;
        _contentPolicy = contentPolicy;
        _leaseTtl = TimeSpan.FromSeconds(
            configuration.GetValue("Dispatch:LeaseTtlSeconds", 360));
        _maxReservationUsd = Math.Max(0.01m,
            configuration.GetValue("Dispatch:MaxReservationUsd", 10m));
        _asyncMediaRetentionHours = Math.Clamp(
            configuration.GetValue("ObjectStorage:AsyncRetentionHours", 24), 1, 24 * 30);
        _batchMediaRetentionHours = Math.Clamp(
            configuration.GetValue("ObjectStorage:BatchRetentionHours", 24 * 7), 1, 24 * 90);
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
                    scopes = auth.Scopes,
                }));
            }
        }
        catch (Exception ex)
        {
            return DispatchResult.Rejected(ClassifyAuthFailure(ex), ex.Message);
        }

        var requestedCapability = string.IsNullOrWhiteSpace(req.Capability)
            ? req.Endpoint : req.Capability;
        if (!ApiKeyScopes.Allows(auth.Scopes, requestedCapability))
        {
            await RecordDeniedScopeAsync(auth, requestedCapability, req.RequestId);
            return DispatchResult.Rejected("unsupportedCapability",
                $"API key is not authorized for capability '{requestedCapability}'");
        }

        var contentDecision = await _contentPolicy.EvaluateAsync(
            auth.UserId, req.RequestId, req.Endpoint, requestedCapability,
            ContentPolicyStage.Request, req.RequestBody);
        if (!contentDecision.Allowed)
        {
            var matched = contentDecision.Matches.FirstOrDefault(match => match.Action == "block");
            return DispatchResult.Rejected("contentPolicyBlocked",
                matched is null
                    ? "Request content was rejected by the active content policy"
                    : $"Request content matched policy rule '{matched.Pattern}'");
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
            if (idempotency.Found && idempotency.HasResponse)
                return DispatchResult.Replay(idempotency.ResponseStatusCode,
                    idempotency.ResponseContentType, idempotency.ResponseBody);
            if (idempotency.Found)
            {
                var existingLease = await _leases.GetByLeaseTokenAsync(idempotency.LeaseToken)
                    ?? await _leases.GetByRequestIdAsync(idempotency.RequestId);
                var recovered = await RecoverExistingDispatchAsync(req, auth, existingLease);
                if (recovered is not null) return recovered;
                return DispatchResult.Rejected("idempotencyReplay",
                    "Request has already been dispatched");
            }
        }

        // A transport retry can arrive without an external idempotency key.
        // request_id is still unique in the durable lease table, so recover an
        // active lease instead of allocating a second hold.
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            var existingLease = await _leases.GetByRequestIdAsync(req.RequestId);
            var recovered = await RecoverExistingDispatchAsync(req, auth, existingLease);
            if (recovered is not null) return recovered;
            if (existingLease is not null)
                return DispatchResult.Rejected("idempotencyReplay",
                    "Request has already been dispatched");
        }

        ModelPrice? requestPrice = null;
        if (!string.IsNullOrWhiteSpace(req.RequestedModel)
            && !_pricing.TryGetPrice(req.RequestedModel, out requestPrice))
        {
            return DispatchResult.Rejected("pricingUnavailable",
                $"Pricing is not configured for model '{req.RequestedModel}'");
        }

        var userGrain = _cluster.GetGrain<IUserGrain>(auth.UserId);

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
        var isControlOperation = capability is "models" or "gemini_models" or "count_tokens"
            or "responses_subpath";
        if (!string.IsNullOrWhiteSpace(req.ForcePlatform)
            && !string.Equals(groupProj.Platform, "composite", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(groupProj.Platform, req.ForcePlatform, StringComparison.OrdinalIgnoreCase))
        {
            return DispatchResult.Rejected("noAccount", "Requested provider is not enabled for this group");
        }
        var selectedGroupId = auth.GroupId;
        var selectedRateMultiplier = await groupGrain.GetEffectiveMultiplier(DateTimeOffset.UtcNow);
        var selection = await schedulerGrain.Select(new SelectRequest(
            req.RequestedModel, req.SessionHash, req.RequestId,
            req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint,
            capability, req.ForcePlatform));

        var attemptedGroups = new HashSet<long> { auth.GroupId };
        var currentProjection = groupProj;
        while (selection.Outcome == SelectionOutcome.Rejected
            && currentProjection.FallbackGroupId is long fallbackId
            && attemptedGroups.Add(fallbackId))
        {
            selectedGroupId = fallbackId;
            var fallbackGroup = _cluster.GetGrain<IGroupGrain>(fallbackId);
            var fallbackProjection = await fallbackGroup.GetAuthProjection();
            if (!string.Equals(fallbackProjection.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                selection = new SelectionResult(SelectionOutcome.Rejected,
                    null, null, null, "Fallback group is disabled");
                currentProjection = fallbackProjection;
                continue;
            }
            if (fallbackProjection.DailyLimitUsd.HasValue &&
                await fallbackGroup.GetDailySpend() >= fallbackProjection.DailyLimitUsd.Value)
            {
                selection = new SelectionResult(SelectionOutcome.Rejected,
                    null, null, null, "Fallback group daily limit reached");
                currentProjection = fallbackProjection;
                continue;
            }
            if (!await fallbackGroup.CheckAndRecordRpm())
            {
                selection = new SelectionResult(SelectionOutcome.Rejected,
                    null, null, null, "Fallback group RPM limit reached");
                currentProjection = fallbackProjection;
                continue;
            }
            selectedRateMultiplier = await fallbackGroup.GetEffectiveMultiplier(DateTimeOffset.UtcNow);
            var fallbackScheduler = _cluster.GetGrain<ISchedulerGrain>(selectedGroupId);
            selection = await fallbackScheduler.Select(new SelectRequest(
                req.RequestedModel, req.SessionHash, req.RequestId,
                req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint,
                capability, req.ForcePlatform));
            currentProjection = fallbackProjection;
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
        // Catalog/token-count calls still use an account lease, but they are
        // not active generations and must not consume the user's generation
        // concurrency budget while the usage outbox is being flushed.
        if (!isControlOperation && !(
            await userGrain.TryAcquireSlot(selection.LeaseToken!, DateTime.UtcNow.Add(_leaseTtl)))
            .Acquired)
        {
            await accountGrain.ReleaseSlot(selection.LeaseToken!);
            return DispatchResult.Rejected("concurrencyExceeded", "User concurrency limit reached");
        }
        if (auth.RpmLimit > 0 && !await userGrain.CheckAndRecordRpm(auth.RpmLimit))
        {
            await accountGrain.ReleaseSlot(selection.LeaseToken!);
            if (!isControlOperation)
                await userGrain.ReleaseSlot(selection.LeaseToken!);
            return DispatchResult.Rejected("rpmExceeded", "User RPM limit reached");
        }
        var holdId = Guid.NewGuid().ToString("N");
        var holdAmount = _maxReservationUsd * Math.Max(1m, selectedRateMultiplier);
        var leaseCreated = false;
        try
        {
            var creds = await _credentials.GetFreshAsync(accountId);
            await accountGrain.RecordRpm();

            var mappedModel = creds.ModelMapping.GetValueOrDefault(
                req.RequestedModel, req.RequestedModel);
            var upstreamPath = ResolveUpstreamPath(
                creds.Platform, req, mappedModel);
            var created = await _leases.CreateDetailedAsync(new LeaseCreateRequest(
                selection.LeaseToken!, req.RequestId, req.ApiKeyHash, auth.ApiKeyId,
                auth.UserId, accountId, selectedGroupId, req.RequestedModel, mappedModel,
                req.Endpoint, selectedRateMultiplier, holdId, holdAmount,
                DateTime.UtcNow.Add(_leaseTtl),
                isMediaOperation ? "" : req.IdempotencyKey,
                isMediaOperation ? "" : req.RequestFingerprint,
                requestPrice));
            if (created.SubscriptionQuotaExceeded)
            {
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("quotaExhausted", "Subscription quota exhausted");
            }
            if (created.InsufficientFunds)
            {
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("noBalance", "Insufficient available balance");
            }
            if (created.Conflict)
            {
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("idempotencyConflict",
                    "Idempotency key was already used for a different request");
            }
            if (!created.Created)
            {
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("idempotencyReplay",
                    "Request has already been dispatched");
            }
            leaseCreated = true;
            _faults.CrashIfConfigured("platform.before_provider_dispatch", req.RequestId);
            _faults.CrashIfConfigured("platform.before_provider_dispatch_retry", req.RequestId);

            var mediaOperationId = "";
            if (isMediaOperation)
            {
                var ttl = capability == "images_async" ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24);
                var retentionUntil = DateTime.UtcNow.AddHours(
                    capability == "images_async"
                        ? _asyncMediaRetentionHours : _batchMediaRetentionHours);
                var media = await _mediaOperations.CreateOrGetAsync(
                    auth.ApiKeyId, accountId, req.RequestId, selection.LeaseToken!,
                    req.Operation, req.IdempotencyKey, req.RequestFingerprint,
                    creds.Platform, DateTime.UtcNow.Add(ttl), retentionUntil);
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
                ProxyUsername = creds.ProxyUsername,
                ProxyPassword = creds.ProxyPassword,
                TlsFingerprint = creds.TlsFingerprint,
                TlsFingerprintProfileId = creds.TlsFingerprintProfileId ?? "",
                ApiKeyId = auth.ApiKeyId,
                UserId = auth.UserId,
                GroupId = selectedGroupId,
                RateMultiplier = selectedRateMultiplier,
                HoldAmount = holdAmount,
                HoldHandle = holdId,
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
            _logger.LogError(ex,
                "Dispatch failed for request {RequestId}, account {AccountId}; applying compensation",
                req.RequestId, accountId);
            if (leaseCreated)
            {
                try
                {
                    await _leases.AbortAsync(selection.LeaseToken!, "dispatch_failed");
                }
                catch (Exception compensationError)
                {
                    _logger.LogError(compensationError,
                        "Failed to abort lease {LeaseToken} during dispatch compensation",
                        selection.LeaseToken);
                }
            }
            try
            {
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
            }
            catch (Exception compensationError)
            {
                _logger.LogError(compensationError,
                    "Failed to release account slot for lease {LeaseToken}", selection.LeaseToken);
            }
            try
            {
                await userGrain.ReleaseSlot(selection.LeaseToken!);
            }
            catch (Exception compensationError)
            {
                _logger.LogError(compensationError,
                    "Failed to release user slot for lease {LeaseToken}", selection.LeaseToken);
            }
            return DispatchResult.Rejected("platformUnavailable",
                "Platform dispatch failed; retry may be safe");
        }
    }

    private static string ClassifyAuthFailure(Exception exception) =>
        exception.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            ? "expired" : "invalidKey";

    private async Task RecordDeniedScopeAsync(AuthResult auth, string capability, string requestId)
    {
        try
        {
            await using var command = _dataSource.CreateCommand("""
                INSERT INTO api_key_audit_events
                    (api_key_id, user_id, actor_user_id, action, scopes,
                     capability, reason, request_id)
                VALUES ($1, $2, $3, 'denied', $4::jsonb, $5, $6, $7)
                """);
            command.Parameters.AddWithValue(auth.ApiKeyId);
            command.Parameters.AddWithValue(auth.UserId);
            command.Parameters.AddWithValue(auth.UserId);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(auth.Scopes));
            command.Parameters.AddWithValue(capability);
            command.Parameters.AddWithValue("scope_denied");
            command.Parameters.AddWithValue(requestId);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Unable to persist API key scope denial for {ApiKeyId} and {RequestId}",
                auth.ApiKeyId, requestId);
        }
    }

    private async Task<DispatchResult?> RecoverExistingDispatchAsync(
        DispatchRequest req, AuthResult auth, RequestLease? lease)
    {
        if (lease is null
            || lease.ApiKeyId != auth.ApiKeyId
            || !string.Equals(lease.ApiKeyHash, req.ApiKeyHash, StringComparison.Ordinal)
            || lease.Status is not ("held" or "forwarded" or "output_started"
                or "reconciliation_needed"))
            return null;

        try
        {
            var accountGrain = _cluster.GetGrain<IAccountGrain>(lease.AccountId);
            var creds = await _credentials.GetFreshAsync(lease.AccountId);
            var capability = string.IsNullOrWhiteSpace(req.Capability) ? req.Endpoint : req.Capability;
            var mappedModel = string.IsNullOrWhiteSpace(lease.UpstreamModel)
                ? req.RequestedModel : lease.UpstreamModel;
            return DispatchResult.Ok(new UpstreamTargetResult
            {
                AccountId = lease.AccountId,
                Platform = creds.Platform,
                BaseUrl = creds.BaseUrl,
                UpstreamPath = ResolveUpstreamPath(creds.Platform, req, mappedModel),
                AuthHeaders = creds.AuthHeaders,
                MappedModel = mappedModel,
                ProxyUrl = creds.ProxyUrl,
                ProxyUsername = creds.ProxyUsername,
                ProxyPassword = creds.ProxyPassword,
                TlsFingerprint = creds.TlsFingerprint,
                TlsFingerprintProfileId = creds.TlsFingerprintProfileId ?? "",
                ApiKeyId = lease.ApiKeyId,
                UserId = lease.UserId,
                GroupId = lease.GroupId,
                RateMultiplier = lease.RateMultiplier,
                HoldAmount = lease.HoldAmount,
                HoldHandle = lease.HoldHandle,
                LeaseToken = lease.LeaseToken,
                AuthVersion = auth.Version,
                HttpMethod = ResolveUpstreamMethod(req),
                UpstreamFormat = ResolveUpstreamFormat(creds.Platform, req),
                AllowedResponseHeaders = [
                    "Content-Type", "Retry-After", "X-Request-ID", "OpenAI-Request-ID",
                    "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset"],
                WebsocketUrl = req.RealtimeSession
                    ? ToWebsocketUrl(creds.BaseUrl, ResolveUpstreamPath(creds.Platform, req, mappedModel))
                    : "",
                CapabilityFlags = [capability],
                PollingSupported = req.Operation.Contains("get", StringComparison.OrdinalIgnoreCase)
                    || capability is "images_async" or "videos",
                ContentDownloadSupported = capability is "images_async" or "images_batch" or "videos",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unable to recover active dispatch lease {LeaseToken} for request {RequestId}",
                lease.LeaseToken, req.RequestId);
            return DispatchResult.Rejected("platformUnavailable",
                "Platform dispatch recovery is temporarily unavailable");
        }
    }

    public async Task<WriteAck> HandleReportUsage(UsageReportRequest req)
    {
        var ack = await _leases.CompleteAsync(new LeaseCompletion(
            req.LeaseToken,
            req.InputTokens, req.OutputTokens, req.CacheCreateTokens,
            req.CacheReadTokens, req.DurationMs, req.FirstTokenMs,
            req.StatusCode, req.Stream, req.ClientDisconnect,
            req.InputImageCount, req.OutputImageCount, req.ImageSize,
            req.VideoCount, req.VideoResolution, req.VideoDurationSeconds,
            req.RealtimeDurationMs, req.RealtimeFrames, req.DisconnectReason,
            req.ProviderUsageJson, req.ReasoningTokens, req.ServiceTier,
            req.UpstreamEndpoint, req.CancellationReason,
            req.MediaOperationId, req.PricingVersion,
            req.ResponseStatusCode, req.ResponseContentType, req.ResponseBody));
        if (ack.Accepted)
            await ReleaseTerminalLeaseSlotsAsync(req.LeaseToken);
        return ack;
    }

    public async Task<ContentPolicyRpcResult> HandleContentPolicy(ContentPolicyRpcRequest req)
    {
        if (req.Stage != ContentPolicyStage.Response)
            return ContentPolicyRpcResult.Error(
                "content_policy_stage_invalid", "Only response-stage lease evaluation is supported");
        if (string.IsNullOrWhiteSpace(req.LeaseToken))
            return ContentPolicyRpcResult.Error(
                "content_policy_lease_required", "A lease token is required");

        var lease = await _leases.GetByLeaseTokenAsync(req.LeaseToken);
        if (lease is null)
            return ContentPolicyRpcResult.Error(
                "content_policy_lease_not_found", "The request lease was not found");
        if (lease.Status is not ("held" or "forwarded" or "output_started"
            or "reconciliation_needed"))
            return ContentPolicyRpcResult.Error(
                "content_policy_lease_terminal", "The request lease is already terminal");

        var capability = string.IsNullOrWhiteSpace(req.Capability)
            ? lease.InboundEndpoint : req.Capability;
        var decision = await _contentPolicy.EvaluateAsync(
            lease.UserId, lease.RequestId, lease.InboundEndpoint, capability,
            ContentPolicyStage.Response, req.Content);
        if (decision.Allowed)
            return ContentPolicyRpcResult.Passed();

        var match = decision.Matches.FirstOrDefault(item => item.Action == "block");
        return ContentPolicyRpcResult.Blocked(
            decision.Code, match?.RuleId ?? 0,
            "Provider response was withheld by the active content policy",
            decision.Retryable);
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

        MediaOperation? operation = req.Action == "lookup_idempotency"
            ? await _mediaOperations.GetByIdempotencyAsync(auth.ApiKeyId, req.IdempotencyKey)
            : string.IsNullOrWhiteSpace(req.OperationId)
                ? null
                : await _mediaOperations.GetAsync(auth.ApiKeyId, req.OperationId);
        var requiredMediaScope = MediaScopeFor(operation?.OperationType);
        var mediaAllowed = requiredMediaScope is null
            ? ApiKeyScopes.Allows(auth.Scopes, "images_async")
                || ApiKeyScopes.Allows(auth.Scopes, "images_batch")
                || ApiKeyScopes.Allows(auth.Scopes, "videos")
            : ApiKeyScopes.Allows(auth.Scopes, requiredMediaScope);
        if (!mediaAllowed)
        {
            await RecordDeniedScopeAsync(auth, requiredMediaScope ?? "media", req.RequestId);
            return MediaOperationRpcResult.Error(403, "unsupported_capability",
                "API key is not authorized for this media operation");
        }

        if (req.Action == "list")
        {
            var batches = await _mediaOperations.ListBatchesAsync(auth.ApiKeyId);
            var payload = JsonSerializer.Serialize(new
            {
                @object = "list",
                data = batches.Select(batch => new
                {
                    id = batch.OperationId,
                    @object = "image.batch",
                    status = batch.Status,
                    progress = batch.Progress,
                    output_url = batch.OutputUrl,
                    error = string.IsNullOrWhiteSpace(batch.Error) ? null : batch.Error,
                }),
                has_more = false,
            });
            return MediaOperationRpcResult.List(payload);
        }

        if (req.Action == "lookup_idempotency")
        {
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
                if (operation is null)
                    return MediaOperationRpcResult.Error(404, "not_found_error", "Media operation not found");
                if (operation.Status is "succeeded" or "failed" or "canceled" or "expired")
                    break;
                if (!string.IsNullOrWhiteSpace(operation.UpstreamTaskId))
                {
                    try
                    {
                        var credentials = await _credentials.GetFreshAsync(
                            operation.AccountId, CancellationToken.None, "media");
                        await _mediaCancellation.CancelAsync(credentials, operation);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex,
                            "Provider cancellation failed for {OperationId}", operation.OperationId);
                        return MediaOperationRpcResult.Error(502, "provider_cancel_failed",
                            "The provider did not accept media cancellation");
                    }
                }
                operation = await _mediaOperations.CancelAsync(auth.ApiKeyId, req.OperationId);
                if (operation?.Status == "canceled")
                    await _leases.AbortAsync(operation.LeaseToken, "media_operation_canceled",
                        LeaseAbortDisposition.Unknown);
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
                if (operation is null)
                    return MediaOperationRpcResult.Error(404, "not_found_error", "Media operation not found");
                try
                {
                    await _objectStorage.DeleteAsync(operation.ObjectKey);
                    foreach (var itemKey in await _mediaOperations.ListItemObjectKeysAsync(
                        operation.OperationId))
                        await _objectStorage.DeleteAsync(itemKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Media object delete failed for {OperationId}",
                        operation.OperationId);
                    return MediaOperationRpcResult.Error(503, "object_storage_unavailable",
                        "Media output could not be deleted");
                }
                return await _mediaOperations.DeleteAsync(auth.ApiKeyId, req.OperationId)
                    ? new MediaOperationRpcResult(true, 204, req.OperationId, "", "", 100,
                        "", "", "", "", "", "")
                    : MediaOperationRpcResult.Error(409, "operation_not_terminal",
                        "Only terminal media operations can be deleted");
            case "delete_outputs":
                if (operation is null)
                    return MediaOperationRpcResult.Error(404, "not_found_error", "Media operation not found");
                try
                {
                    await _objectStorage.DeleteAsync(operation.ObjectKey);
                    foreach (var itemKey in await _mediaOperations.ListItemObjectKeysAsync(
                        operation.OperationId))
                        await _objectStorage.DeleteAsync(itemKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Media output delete failed for {OperationId}",
                        operation.OperationId);
                    return MediaOperationRpcResult.Error(503, "object_storage_unavailable",
                        "Media output could not be deleted");
                }
                operation = await _mediaOperations.ClearOutputsAsync(auth.ApiKeyId, req.OperationId);
                break;
            default:
                break;
        }

        if (operation is null)
            return MediaOperationRpcResult.Error(404, "not_found_error", "Media operation not found");
        if (req.Action == "items")
        {
            var items = await _mediaOperations.ListItemsAsync(auth.ApiKeyId,
                operation.OperationId);
            if (items.Count > 0)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    data = items.Select(item => new
                    {
                        custom_id = item.CustomId,
                        url = item.ObjectStatus == "stored" && !string.IsNullOrWhiteSpace(item.ObjectKey)
                            ? _objectStorage.PresignGet(item.ObjectKey, TimeSpan.FromHours(1))
                            : "",
                        content_type = item.ContentType,
                        size = item.ObjectSize,
                        status = item.ObjectStatus,
                        error = string.IsNullOrWhiteSpace(item.Error) ? null : item.Error,
                    }),
                });
                return MediaOperationRpcResult.From(operation) with
                {
                    OutputMetadata = payload,
                };
            }
        }
        if (req.Action is "content" or "download"
            && (operation.Status != "succeeded" || string.IsNullOrWhiteSpace(operation.OutputUrl)))
            return MediaOperationRpcResult.Error(409, "output_not_ready", "Media output is not ready");
        return MediaOperationRpcResult.From(operation);
    }

    private static string? MediaScopeFor(string? operationType) =>
        operationType switch
        {
            null or "" => null,
            var value when value.StartsWith("videos", StringComparison.Ordinal) => "videos",
            var value when value.StartsWith("images_batch", StringComparison.Ordinal) => "images_batch",
            _ => "images_async",
        };

    public async Task<WriteAck> HandleAbort(string leaseToken, string reason,
        LeaseAbortDisposition disposition, int? providerStatusCode)
    {
        var ack = await _leases.AbortAsync(leaseToken, reason, disposition, providerStatusCode,
            source: "gateway");
        if (ack.Accepted)
            await ReleaseTerminalLeaseSlotsAsync(leaseToken);
        return ack;
    }

    private async Task ReleaseTerminalLeaseSlotsAsync(string leaseToken)
    {
        var lease = await _leases.GetByLeaseTokenAsync(leaseToken);
        if (lease is null)
            return;

        try
        {
            await _cluster.GetGrain<IAccountGrain>(lease.AccountId)
                .ReleaseSlot(lease.LeaseToken);
            await _cluster.GetGrain<IUserGrain>(lease.UserId)
                .FinalizeLease(lease.LeaseToken, lease.RequestId);
        }
        catch (Exception ex)
        {
            // The durable outbox retries this cleanup after a transient grain
            // failure; a successful billing write must remain acknowledged.
            _logger.LogWarning(ex,
                "Synchronous lease slot release failed for {LeaseToken}; outbox will retry",
                leaseToken);
        }
    }

    public async Task<WriteAck> HandleLeaseEvidence(string leaseToken,
        LeaseEvidenceStage stage, string source, string detail)
    {
        return await _leases.RecordEvidenceAsync(leaseToken, stage, source, detail);
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
    string RequestFingerprint = "", string RequestQuery = "", string RequestBody = "");

public record UsageReportRequest(
    string LeaseToken, int InputTokens, int OutputTokens,
    int CacheCreateTokens, int CacheReadTokens, int DurationMs,
    int FirstTokenMs, int StatusCode, bool Stream, bool ClientDisconnect,
    int InputImageCount = 0, int OutputImageCount = 0, string ImageSize = "",
    int VideoCount = 0, string VideoResolution = "", int VideoDurationSeconds = 0,
    int RealtimeDurationMs = 0, int RealtimeFrames = 0, string DisconnectReason = "",
    string ProviderUsageJson = "", int ReasoningTokens = 0, string ServiceTier = "",
    string UpstreamEndpoint = "", string CancellationReason = "",
    string MediaOperationId = "", string PricingVersion = "",
    int ResponseStatusCode = 0, string ResponseContentType = "", string ResponseBody = "");

public sealed record MediaOperationRpcRequest(
    string ApiKeyHash, string OperationId, string Action, string RequestId,
    string ClientIp, string IdempotencyKey, string RequestFingerprint,
    string Status, string UpstreamTaskId, string OutputMetadata,
    string OutputUrl, string ContentType, int Progress);

public sealed record ContentPolicyRpcRequest(
    string LeaseToken, string Content, string Capability, ContentPolicyStage Stage);

public sealed record ContentPolicyRpcResult(
    bool Evaluated, bool Allowed, bool Retryable, string ErrorCode,
    long MatchedRuleId, string Message)
{
    public static ContentPolicyRpcResult Passed() => new(true, true, false, "", 0, "");
    public static ContentPolicyRpcResult Blocked(
        string code, long ruleId, string message, bool retryable = false) =>
        new(true, false, retryable, code, ruleId, message);
    public static ContentPolicyRpcResult Error(
        string code, string message, bool retryable = false) =>
        new(false, false, retryable, code, 0, message);
}

public sealed record MediaOperationRpcResult(
    bool Accepted, int StatusCode, string OperationId, string OperationType,
    string Status, int Progress, string UpstreamTaskId, string OutputMetadata,
    string OutputUrl, string ContentType, string ErrorCode, string ErrorMessage)
{
    public static MediaOperationRpcResult List(string outputMetadata) => new(
        true, 200, "", "images_batch_list", "completed", 100, "", outputMetadata,
        "", "", "", "");

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
    public string? ProxyUsername { get; init; }
    public string? ProxyPassword { get; init; }
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
    public ushort ProtocolVersion { get; init; } = 3;
    public int ReplayStatusCode { get; init; }
    public string ReplayContentType { get; init; } = "";
    public string ReplayBody { get; init; } = "";

    public static DispatchResult Ok(UpstreamTargetResult upstream) =>
        new() { Outcome = "ok", Upstream = upstream, AuthVersion = upstream.AuthVersion };

    public static DispatchResult Rejected(string code, string message) =>
        new() { Outcome = "rejected", RejectCode = code, RejectMessage = message };

    public static DispatchResult Replay(int statusCode, string contentType, string body) =>
        new()
        {
            Outcome = "rejected",
            RejectCode = "idempotencyReplay",
            RejectMessage = "Request has already been dispatched",
            ReplayStatusCode = statusCode,
            ReplayContentType = contentType,
            ReplayBody = body,
        };

    public static DispatchResult Wait(int timeoutMs) =>
        new() { Outcome = "wait", WaitTimeoutMs = timeoutMs };
}
