using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Sub2Api.Grains.Interfaces;
using Sub2Api.Data.Migration;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using Capnp;
using CapnpGen;

namespace Sub2Api.Host.Services;

public class CapnpRpcHostedService : IHostedService
{
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
        _socketPath = configuration["CapnpRpc:SocketPath"] ?? "/var/run/sub2api/dispatch.sock";
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
                _ => SerializeRejectResponse("unknown method"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RPC processing error");
            return method is 2 or 3 or 4
                ? SerializeWriteAck((byte)(0x80 + method), WriteAck.Error("platform_error", retryable: true))
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
                _ => "unknown",
            },
            MetadataUserId: capnpReq.MetadataUserId,
            Stream: capnpReq.Stream);

        var result = await svc.HandleDispatch(req);
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

            if (!string.IsNullOrEmpty(result.Upstream.ProxyUrl))
            {
                var proxy = up.Proxy;
                proxy.Enabled = true;
                proxy.Url = result.Upstream.ProxyUrl;
            }

            var billing = up.Billing;
            billing.RateMultiplier = result.Upstream.RateMultiplier;
            billing.HoldHandle = result.Upstream.HoldHandle ?? "";

            if (result.Upstream.AuthHeaders.Count > 0)
            {
                var headers = result.Upstream.AuthHeaders
                    .Select(kv => new CapnpGen.UpstreamTarget.Header { Key = kv.Key, Value = kv.Value })
                    .ToList();
                up.AuthHeaders.Init(headers, (w, v) => { w.Key = v.Key; w.Value = v.Value; });
            }
        }

        writer.ProtocolVersion = 2;
        return SerializeToFrame(0x81, message.Frame);
    }

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
            ClientDisconnect: report.ClientDisconnect);

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
    private readonly AuthProjectionCache _authCache;
    private readonly GarnetWriteThroughService _garnet;
    private readonly ILogger<DispatchService> _logger;
    private readonly MigrationWriteGate _writeGate;
    private readonly TimeSpan _leaseTtl;
    private readonly decimal _maxReservationUsd;

    public DispatchService(IClusterClient cluster, RequestLeaseStore leases,
                           AuthProjectionCache authCache, GarnetWriteThroughService garnet,
                           MigrationWriteGate writeGate, IConfiguration configuration,
                           ILogger<DispatchService> logger)
    {
        _cluster = cluster;
        _leases = leases;
        _authCache = authCache;
        _garnet = garnet;
        _writeGate = writeGate;
        _logger = logger;
        _leaseTtl = TimeSpan.FromSeconds(
            configuration.GetValue("Dispatch:LeaseTtlSeconds", 360));
        _maxReservationUsd = Math.Max(0.01m,
            configuration.GetValue("Dispatch:MaxReservationUsd", 10m));
    }

    public async Task<DispatchResult> HandleDispatch(DispatchRequest req)
    {
        try
        {
            await _writeGate.AssertPlatformPrimaryAsync();
        }
        catch (MigrationWriteRejectedException ex)
        {
            _logger.LogDebug(ex, "Dispatch rejected by migration fence for request {RequestId}", req.RequestId);
            return DispatchResult.Rejected("migrationFence", "Platform is not the current write primary");
        }
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
                    rate_multiplier = auth.RateMultiplier,
                    rpm_limit = auth.RpmLimit,
                }));
            }
        }
        catch (Exception ex)
        {
            return DispatchResult.Rejected("invalidKey", ex.Message);
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
        var selectedGroupId = auth.GroupId;
        var selectedRateMultiplier = auth.RateMultiplier;
        var selection = await schedulerGrain.Select(new SelectRequest(
            req.RequestedModel, req.SessionHash, req.RequestId,
            req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint));

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
                req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint));
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
                Math.Max(1m, (decimal)selectedRateMultiplier);
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
                creds.Platform, req.Endpoint, mappedModel, req.Stream);
            var created = await _leases.CreateAsync(new LeaseCreateRequest(
                selection.LeaseToken!, req.RequestId, req.ApiKeyHash, auth.ApiKeyId,
                auth.UserId, accountId, selectedGroupId, req.RequestedModel, mappedModel,
                req.Endpoint, (decimal)selectedRateMultiplier, hold.Id, hold.Amount,
                DateTime.UtcNow.Add(_leaseTtl)));
            if (!created)
            {
                await userGrain.ReleaseHold(hold);
                await accountGrain.ReleaseSlot(selection.LeaseToken!);
                await userGrain.ReleaseSlot(selection.LeaseToken!);
                return DispatchResult.Rejected("duplicateRequest", "Request has already been dispatched");
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
                HoldHandle = hold.Id,
                LeaseToken = selection.LeaseToken!,
                AuthVersion = auth.Version,
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
            req.StatusCode, req.Stream, req.ClientDisconnect));
    }

    public async Task<WriteAck> HandleAbort(string leaseToken, string reason)
    {
        return await _leases.AbortAsync(leaseToken, reason);
    }

    public async Task HandleUpstreamError(long accountId, int statusCode, int? retryAfterMs)
    {
        try
        {
            await _writeGate.AssertPlatformPrimaryAsync();
        }
        catch (MigrationWriteRejectedException ex)
        {
            _logger.LogDebug(ex, "Upstream error write rejected by migration fence for account {AccountId}", accountId);
            return;
        }
        var accountGrain = _cluster.GetGrain<IAccountGrain>(accountId);
        await accountGrain.ReportUpstreamError(new ErrorInfo(statusCode, retryAfterMs, null));
    }

    private static string ResolveUpstreamPath(string platform, string endpoint,
        string mappedModel, bool stream)
    {
        if (platform is "anthropic" or "claude") return "/v1/messages";
        if (platform is "gemini" or "google")
            return stream
                ? $"/v1beta/models/{Uri.EscapeDataString(mappedModel)}:streamGenerateContent?alt=sse"
                : $"/v1beta/models/{Uri.EscapeDataString(mappedModel)}:generateContent";
        return endpoint == "responses" ? "/v1/responses" : "/v1/chat/completions";
    }
}

public record DispatchRequest(
    string ApiKeyHash, string RequestedModel, string SessionHash,
    string ClientIp, string RequestId, long[] ExcludedAccountIds,
    long CachedAuthVersion, string Endpoint, string? MetadataUserId, bool Stream);

public record UsageReportRequest(
    string LeaseToken, int InputTokens, int OutputTokens,
    int CacheCreateTokens, int CacheReadTokens, int DurationMs,
    int FirstTokenMs, int StatusCode, bool Stream, bool ClientDisconnect);

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
    public double RateMultiplier { get; init; }
    public string? HoldHandle { get; init; }
    public string LeaseToken { get; init; } = "";
    public long AuthVersion { get; init; }
}

public record DispatchResult
{
    public string Outcome { get; init; } = "ok";
    public UpstreamTargetResult? Upstream { get; init; }
    public string? RejectCode { get; init; }
    public string? RejectMessage { get; init; }
    public int WaitTimeoutMs { get; init; }
    public long AuthVersion { get; init; }

    public static DispatchResult Ok(UpstreamTargetResult upstream) =>
        new() { Outcome = "ok", Upstream = upstream, AuthVersion = upstream.AuthVersion };

    public static DispatchResult Rejected(string code, string message) =>
        new() { Outcome = "rejected", RejectCode = code, RejectMessage = message };

    public static DispatchResult Wait(int timeoutMs) =>
        new() { Outcome = "wait", WaitTimeoutMs = timeoutMs };
}
