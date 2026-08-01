using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Sub2Api.Grains.Interfaces;
using Sub2Api.Data.Entities;
using Sub2Api.Data.Infrastructure;
using System.Buffers.Binary;
using System.Net.Sockets;
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
            return SerializeRejectResponse(ex.Message);
        }
    }

    private static DeserializerState DeserializeRoot(ReadOnlyMemory<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var reader = new BinaryReader(ms);
        var frame = Framing.ReadWireFrame(reader);
        return DeserializerState.CreateRoot(frame);
    }

    private static byte[] SerializeToFrame(byte responseMethod, SerializerState state)
    {
        DeserializerState dState = state;
        var frame = new WireFrame(dState.Segments);
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
        var capnpReq = CapnpSerializable.Create<CapnpGen.DispatchRequest>(state);

        var req = new DispatchRequest(
            ApiKeyHash: capnpReq.ApiKeyHash ?? "",
            RequestedModel: capnpReq.RequestedModel ?? "",
            SessionHash: capnpReq.SessionHash ?? "",
            ClientIp: capnpReq.ClientIp ?? "",
            RequestId: capnpReq.RequestId ?? "",
            ExcludedAccountIds: capnpReq.ExcludedAccounts?.ToArray() ?? [],
            CachedAuthVersion: capnpReq.CachedAuthVersion,
            Endpoint: ((int)capnpReq.Endpoint).ToString(),
            MetadataUserId: capnpReq.MetadataUserId);

        var result = await svc.HandleDispatch(req);
        return BuildDispatchResponse(result);
    }

    private static byte[] BuildDispatchResponse(DispatchResult result)
    {
        var writer = SerializerState.CreateForRpc<CapnpGen.DispatchResponse.WRITER>();

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
            var up = writer.Upstream;
            up.AccountId = result.Upstream.AccountId;
            up.Platform = result.Upstream.Platform;
            up.BaseUrl = result.Upstream.BaseUrl;
            up.UpstreamPath = "";
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

        return SerializeToFrame(0x81, writer);
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
        var report = CapnpSerializable.Create<CapnpGen.UsageReport>(state);

        var req = new UsageReportRequest(
            LeaseToken: report.LeaseToken ?? "",
            RequestId: report.RequestId ?? "",
            ApiKeyHash: "",
            ApiKeyId: report.ApiKeyId,
            UserId: report.UserId,
            AccountId: report.AccountId,
            GroupId: report.GroupId,
            Model: report.Model ?? "",
            UpstreamModel: report.UpstreamModel ?? "",
            InputTokens: report.InputTokens,
            OutputTokens: report.OutputTokens,
            CacheCreateTokens: report.CacheCreateTokens,
            CacheReadTokens: report.CacheReadTokens,
            DurationMs: report.DurationMs,
            FirstTokenMs: report.FirstTokenMs,
            Stream: report.Stream,
            ClientDisconnect: report.ClientDisconnect,
            RateMultiplier: 1.0,
            HoldHandle: null);

        await svc.HandleReportUsage(req);
        return SerializeEmptyResponse(0x82);
    }

    private static async Task<byte[]> HandleAbortAsync(DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var reader = CapnpGen.GatewayDispatch.Params_Abort.READER.create(state);
        var leaseToken = reader.LeaseToken ?? "";
        var reason = reader.Reason ?? "";

        await svc.HandleAbort(leaseToken, reason, 0, 0);
        return SerializeEmptyResponse(0x83);
    }

    private static async Task<byte[]> HandleUpstreamErrorAsync(DispatchService svc, ReadOnlyMemory<byte> capnpData)
    {
        var state = DeserializeRoot(capnpData);
        var report = CapnpSerializable.Create<CapnpGen.ErrorReport>(state);

        await svc.HandleUpstreamError(report.AccountId, report.StatusCode,
            report.RetryAfterMs > 0 ? report.RetryAfterMs : null);
        return SerializeEmptyResponse(0x84);
    }

    private static byte[] SerializeEmptyResponse(byte method)
    {
        return [method];
    }

    private static byte[] SerializeRejectResponse(string message)
    {
        var writer = SerializerState.CreateForRpc<CapnpGen.DispatchResponse.WRITER>();
        writer.TheOutcome = CapnpGen.DispatchResponse.Outcome.rejected;
        writer.Reject.Message = message;
        writer.Reject.Code = CapnpGen.RejectInfo.RejectCode.invalidKey;
        return SerializeToFrame(0x81, writer);
    }
}

public class DispatchService
{
    private readonly IClusterClient _cluster;
    private readonly GarnetWriteThroughService _garnet;
    private readonly ModelPricingService _pricing;
    private readonly BatchWriter<UsageLogEntity> _usageWriter;
    private readonly ILogger<DispatchService> _logger;

    public DispatchService(IClusterClient cluster, GarnetWriteThroughService garnet,
                           ModelPricingService pricing, BatchWriter<UsageLogEntity> usageWriter,
                           ILogger<DispatchService> logger)
    {
        _cluster = cluster;
        _garnet = garnet;
        _pricing = pricing;
        _usageWriter = usageWriter;
        _logger = logger;
    }

    public async Task<DispatchResult> HandleDispatch(DispatchRequest req)
    {
        var apiKeyGrain = _cluster.GetGrain<IApiKeyGrain>(req.ApiKeyHash);

        AuthResult auth;
        try
        {
            var currentVersion = await apiKeyGrain.GetVersion();
            if (req.CachedAuthVersion == currentVersion && req.CachedAuthVersion > 0)
            {
                auth = await apiKeyGrain.Validate(new AuthRequest(req.ClientIp, req.RequestId));
            }
            else
            {
                auth = await apiKeyGrain.Validate(new AuthRequest(req.ClientIp, req.RequestId));
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

        var userSlot = await userGrain.TryAcquireSlot(req.RequestId);
        if (!userSlot.Acquired)
        {
            return DispatchResult.Rejected("concurrencyExceeded", "User concurrency limit reached");
        }

        if (auth.RpmLimit > 0 && !await userGrain.CheckAndRecordRpm(auth.RpmLimit))
        {
            await userGrain.ReleaseSlot(req.RequestId);
            return DispatchResult.Rejected("rpmExceeded", "User RPM limit reached");
        }

        var groupGrain = _cluster.GetGrain<IGroupGrain>(auth.GroupId);
        var groupProj = await groupGrain.GetAuthProjection();

        if (groupProj.DailyLimitUsd.HasValue)
        {
            var dailySpend = await groupGrain.GetDailySpend();
            if (dailySpend >= groupProj.DailyLimitUsd.Value)
            {
                await userGrain.ReleaseSlot(req.RequestId);
                return DispatchResult.Rejected("quotaExhausted", "Group daily limit reached");
            }
        }

        var schedulerGrain = _cluster.GetGrain<ISchedulerGrain>(auth.GroupId);
        var selection = await schedulerGrain.Select(new SelectRequest(
            req.RequestedModel, req.SessionHash, req.RequestId,
            req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint));

        if (selection.Outcome == SelectionOutcome.Rejected && groupProj.FallbackGroupId.HasValue)
        {
            var fallbackScheduler = _cluster.GetGrain<ISchedulerGrain>(groupProj.FallbackGroupId.Value);
            selection = await fallbackScheduler.Select(new SelectRequest(
                req.RequestedModel, req.SessionHash, req.RequestId,
                req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint));
        }

        if (selection.Outcome == SelectionOutcome.Rejected)
        {
            await userGrain.ReleaseSlot(req.RequestId);
            return DispatchResult.Rejected("noAccount", selection.RejectReason ?? "No accounts");
        }

        if (selection.Outcome == SelectionOutcome.Wait)
        {
            await userGrain.ReleaseSlot(req.RequestId);
            return DispatchResult.Wait(selection.WaitTimeoutMs ?? 45_000);
        }

        var accountGrain = _cluster.GetGrain<IAccountGrain>(selection.AccountId!.Value);
        var creds = await accountGrain.Hydrate();
        await accountGrain.RecordRpm();

        var hold = await userGrain.ReserveBalance(1.00m);

        _garnet.WriteStickySession(auth.GroupId, req.SessionHash,
            selection.AccountId.Value, TimeSpan.FromHours(1));

        return DispatchResult.Ok(new UpstreamTargetResult
        {
            AccountId = selection.AccountId.Value,
            Platform = creds.Platform,
            BaseUrl = creds.BaseUrl,
            AuthHeaders = creds.AuthHeaders,
            MappedModel = creds.ModelMapping.GetValueOrDefault(req.RequestedModel, req.RequestedModel),
            ProxyUrl = creds.ProxyUrl,
            TlsFingerprint = creds.TlsFingerprint,
            UserId = auth.UserId,
            GroupId = auth.GroupId,
            RateMultiplier = auth.RateMultiplier,
            HoldHandle = hold?.Id,
            LeaseToken = selection.LeaseToken!,
            AuthVersion = auth.Version,
        });
    }

    public async Task HandleReportUsage(UsageReportRequest req)
    {
        var accountGrain = _cluster.GetGrain<IAccountGrain>(req.AccountId);
        await accountGrain.ReleaseSlot(req.RequestId);

        var userGrain = _cluster.GetGrain<IUserGrain>(req.UserId);
        await userGrain.ReleaseSlot(req.RequestId);

        if (req.HoldHandle is not null)
        {
            var cost = ComputeCost(req);
            await userGrain.CommitUsage(
                new HoldHandle(req.HoldHandle, 1.00m), cost);

            var groupGrain = _cluster.GetGrain<IGroupGrain>(req.GroupId);
            await groupGrain.RecordSpend((double)cost);
        }

        var usageGrain = _cluster.GetGrain<IUsageGrain>($"u:{req.UserId}");
        await usageGrain.Record(new UsageEventData(
            req.LeaseToken, req.RequestId, req.ApiKeyId, req.UserId,
            req.AccountId, req.GroupId, req.Model, req.UpstreamModel,
            req.InputTokens, req.OutputTokens, req.CacheCreateTokens,
            req.CacheReadTokens, req.DurationMs, req.FirstTokenMs,
            req.Stream, req.ClientDisconnect));

        var apiKeyGrain = _cluster.GetGrain<IApiKeyGrain>(req.ApiKeyHash);
        await apiKeyGrain.AddUsage(ComputeCost(req));

        _usageWriter.Enqueue(new UsageLogEntity
        {
            RequestId = req.RequestId,
            LeaseToken = req.LeaseToken,
            ApiKeyId = req.ApiKeyId,
            UserId = req.UserId,
            AccountId = req.AccountId,
            GroupId = req.GroupId,
            Model = req.Model,
            UpstreamModel = req.UpstreamModel,
            InputTokens = req.InputTokens,
            OutputTokens = req.OutputTokens,
            CacheCreateTokens = req.CacheCreateTokens,
            CacheReadTokens = req.CacheReadTokens,
            CostUsd = ComputeCost(req),
            DurationMs = req.DurationMs,
            FirstTokenMs = req.FirstTokenMs,
            Stream = req.Stream,
            ClientDisconnect = req.ClientDisconnect,
            CreatedAt = DateTime.UtcNow,
        });
    }

    public async Task HandleAbort(string leaseToken, string requestId,
                                   long accountId, long userId)
    {
        var accountGrain = _cluster.GetGrain<IAccountGrain>(accountId);
        await accountGrain.ReleaseSlot(requestId);

        var userGrain = _cluster.GetGrain<IUserGrain>(userId);
        await userGrain.ReleaseSlot(requestId);
    }

    public async Task HandleUpstreamError(long accountId, int statusCode, int? retryAfterMs)
    {
        var accountGrain = _cluster.GetGrain<IAccountGrain>(accountId);
        await accountGrain.ReportUpstreamError(new ErrorInfo(statusCode, retryAfterMs, null));
    }

    private decimal ComputeCost(UsageReportRequest req)
    {
        var price = _pricing.GetPrice(req.Model);
        var inputCost = req.InputTokens * price.InputPerMillion / 1_000_000m;
        var outputCost = req.OutputTokens * price.OutputPerMillion / 1_000_000m;
        var cacheCreateCost = req.CacheCreateTokens * price.CacheCreatePerMillion / 1_000_000m;
        var cacheReadCost = req.CacheReadTokens * price.CacheReadPerMillion / 1_000_000m;
        return (inputCost + outputCost + cacheCreateCost + cacheReadCost) * (decimal)req.RateMultiplier;
    }
}

public record DispatchRequest(
    string ApiKeyHash, string RequestedModel, string SessionHash,
    string ClientIp, string RequestId, long[] ExcludedAccountIds,
    long CachedAuthVersion, string Endpoint, string? MetadataUserId);

public record UsageReportRequest(
    string LeaseToken, string RequestId, string ApiKeyHash,
    long ApiKeyId, long UserId, long AccountId, long GroupId,
    string Model, string UpstreamModel, int InputTokens, int OutputTokens,
    int CacheCreateTokens, int CacheReadTokens, int DurationMs,
    int FirstTokenMs, bool Stream, bool ClientDisconnect,
    double RateMultiplier, string? HoldHandle);

public record UpstreamTargetResult
{
    public long AccountId { get; init; }
    public string Platform { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public Dictionary<string, string> AuthHeaders { get; init; } = new();
    public string MappedModel { get; init; } = "";
    public string? ProxyUrl { get; init; }
    public bool TlsFingerprint { get; init; }
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
