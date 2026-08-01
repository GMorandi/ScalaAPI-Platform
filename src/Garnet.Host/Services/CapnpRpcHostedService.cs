using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Sub2Api.Grains.Interfaces;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

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

        _logger.LogInformation("Dispatch RPC server listening on {Path}", _socketPath);
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

                var response = await ProcessMessageAsync(payload);
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

    private async Task<byte[]> ProcessMessageAsync(byte[] payload)
    {
        var dispatchService = _services.GetRequiredService<DispatchService>();

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();

            return method switch
            {
                "dispatch" => await HandleDispatchAsync(dispatchService, root),
                "reportUsage" => await HandleReportUsageAsync(dispatchService, root),
                "abort" => await HandleAbortAsync(dispatchService, root),
                "reportUpstreamError" => await HandleUpstreamErrorAsync(dispatchService, root),
                _ => SerializeError("unknown method"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RPC processing error");
            return SerializeError(ex.Message);
        }
    }

    private static async Task<byte[]> HandleDispatchAsync(DispatchService svc, JsonElement root)
    {
        var excluded = root.TryGetProperty("excludedAccounts", out var ea)
            ? ea.EnumerateArray().Select(e => e.GetInt64()).ToArray()
            : [];

        var req = new DispatchRequest(
            ApiKeyHash: root.GetProperty("apiKeyHash").GetString() ?? "",
            RequestedModel: root.GetProperty("requestedModel").GetString() ?? "",
            SessionHash: root.GetProperty("sessionHash").GetString() ?? "",
            ClientIp: root.GetProperty("clientIp").GetString() ?? "",
            RequestId: root.GetProperty("requestId").GetString() ?? "",
            ExcludedAccountIds: excluded,
            CachedAuthVersion: root.TryGetProperty("cachedAuthVersion", out var cav) ? cav.GetInt64() : 0,
            Endpoint: root.TryGetProperty("endpoint", out var ep) ? ep.GetInt32().ToString() : "1",
            MetadataUserId: root.TryGetProperty("metadataUserId", out var mui) ? mui.GetString() : null);

        var result = await svc.HandleDispatch(req);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            outcome = result.Outcome,
            authVersion = result.AuthVersion,
            leaseToken = result.Upstream?.LeaseToken ?? "",
            rejectMessage = result.RejectMessage ?? "",
            rejectCode = 0,
            waitTimeoutMs = result.WaitTimeoutMs,
            accountId = result.Upstream?.AccountId ?? 0,
            platform = result.Upstream?.Platform ?? "",
            baseUrl = result.Upstream?.BaseUrl ?? "",
            upstreamPath = "",
            mappedModel = result.Upstream?.MappedModel ?? "",
            proxyUrl = result.Upstream?.ProxyUrl ?? "",
            userId = result.Upstream?.UserId ?? 0,
            groupId = result.Upstream?.GroupId ?? 0,
            rateMultiplier = result.Upstream?.RateMultiplier ?? 1.0,
            holdHandle = result.Upstream?.HoldHandle ?? "",
            tlsFingerprint = result.Upstream?.TlsFingerprint ?? false,
            authHeaders = result.Upstream?.AuthHeaders
                .Select(kv => new { key = kv.Key, value = kv.Value }).ToArray() ?? [],
        });
    }

    private static async Task<byte[]> HandleReportUsageAsync(DispatchService svc, JsonElement root)
    {
        var req = new UsageReportRequest(
            LeaseToken: root.GetProperty("leaseToken").GetString() ?? "",
            RequestId: root.GetProperty("requestId").GetString() ?? "",
            ApiKeyHash: "",
            ApiKeyId: root.TryGetProperty("apiKeyId", out var aki) ? aki.GetInt64() : 0,
            UserId: root.TryGetProperty("userId", out var ui) ? ui.GetInt64() : 0,
            AccountId: root.TryGetProperty("accountId", out var ai) ? ai.GetInt64() : 0,
            GroupId: root.TryGetProperty("groupId", out var gi) ? gi.GetInt64() : 0,
            Model: root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            UpstreamModel: root.TryGetProperty("upstreamModel", out var um) ? um.GetString() ?? "" : "",
            InputTokens: root.TryGetProperty("inputTokens", out var it) ? it.GetInt32() : 0,
            OutputTokens: root.TryGetProperty("outputTokens", out var ot) ? ot.GetInt32() : 0,
            CacheCreateTokens: root.TryGetProperty("cacheCreateTokens", out var cct) ? cct.GetInt32() : 0,
            CacheReadTokens: root.TryGetProperty("cacheReadTokens", out var crt) ? crt.GetInt32() : 0,
            DurationMs: root.TryGetProperty("durationMs", out var dm) ? dm.GetInt32() : 0,
            FirstTokenMs: root.TryGetProperty("firstTokenMs", out var ftm) ? ftm.GetInt32() : 0,
            Stream: root.TryGetProperty("stream", out var s) && s.GetBoolean(),
            ClientDisconnect: root.TryGetProperty("clientDisconnect", out var cd) && cd.GetBoolean(),
            RateMultiplier: 1.0,
            HoldHandle: null);

        await svc.HandleReportUsage(req);
        return "{}"u8.ToArray();
    }

    private static async Task<byte[]> HandleAbortAsync(DispatchService svc, JsonElement root)
    {
        var leaseToken = root.GetProperty("leaseToken").GetString() ?? "";
        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
        await svc.HandleAbort(leaseToken, reason, 0, 0);
        return "{}"u8.ToArray();
    }

    private static async Task<byte[]> HandleUpstreamErrorAsync(DispatchService svc, JsonElement root)
    {
        var accountId = root.TryGetProperty("accountId", out var ai) ? ai.GetInt64() : 0;
        var statusCode = root.TryGetProperty("statusCode", out var sc) ? sc.GetInt32() : 0;
        var retryAfterMs = root.TryGetProperty("retryAfterMs", out var ram) ? ram.GetInt32() : 0;
        await svc.HandleUpstreamError(accountId, statusCode, retryAfterMs > 0 ? retryAfterMs : null);
        return "{}"u8.ToArray();
    }

    private static byte[] SerializeError(string message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new { outcome = "rejected", rejectMessage = message });
    }
}

public class DispatchService
{
    private readonly IClusterClient _cluster;
    private readonly GarnetWriteThroughService _garnet;
    private readonly ILogger<DispatchService> _logger;

    public DispatchService(IClusterClient cluster, GarnetWriteThroughService garnet,
                           ILogger<DispatchService> logger)
    {
        _cluster = cluster;
        _garnet = garnet;
        _logger = logger;
    }

    public async Task<DispatchResult> HandleDispatch(DispatchRequest req)
    {
        // Step 1: Validate API key (with version fast-path)
        var apiKeyGrain = _cluster.GetGrain<IApiKeyGrain>(req.ApiKeyHash);

        AuthResult auth;
        try
        {
            var currentVersion = await apiKeyGrain.GetVersion();
            if (req.CachedAuthVersion == currentVersion && req.CachedAuthVersion > 0)
            {
                // Fast path: gateway's cached auth is still valid
                // Still need to validate for IP/status changes
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

        // Step 2: Check user balance
        var userGrain = _cluster.GetGrain<IUserGrain>(auth.UserId);
        if (!await userGrain.CheckBalance(0.01m))
        {
            return DispatchResult.Rejected("noBalance", "Insufficient balance");
        }

        // Step 3: Acquire user concurrency slot
        var userSlot = await userGrain.TryAcquireSlot(req.RequestId);
        if (!userSlot.Acquired)
        {
            return DispatchResult.Rejected("concurrencyExceeded", "User concurrency limit reached");
        }

        // Step 4: Schedule account selection
        var schedulerGrain = _cluster.GetGrain<ISchedulerGrain>(auth.GroupId);
        var selection = await schedulerGrain.Select(new SelectRequest(
            req.RequestedModel, req.SessionHash, req.RequestId,
            req.MetadataUserId, req.ExcludedAccountIds, req.Endpoint));

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

        // Step 5: Hydrate account credentials
        var accountGrain = _cluster.GetGrain<IAccountGrain>(selection.AccountId!.Value);
        var creds = await accountGrain.Hydrate();

        // Step 6: Reserve balance hold
        var hold = await userGrain.ReserveBalance(1.00m);

        // Step 7: Write-through to Garnet for future fast-path reads
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
        // Release account slot
        var accountGrain = _cluster.GetGrain<IAccountGrain>(req.AccountId);
        await accountGrain.ReleaseSlot(req.RequestId);

        // Release user slot
        var userGrain = _cluster.GetGrain<IUserGrain>(req.UserId);
        await userGrain.ReleaseSlot(req.RequestId);

        // Commit billing
        if (req.HoldHandle is not null)
        {
            var cost = ComputeCost(req);
            await userGrain.CommitUsage(
                new HoldHandle(req.HoldHandle, 1.00m), cost);
        }

        // Record usage
        var usageGrain = _cluster.GetGrain<IUsageGrain>($"u:{req.UserId}");
        await usageGrain.Record(new UsageEventData(
            req.LeaseToken, req.RequestId, req.ApiKeyId, req.UserId,
            req.AccountId, req.GroupId, req.Model, req.UpstreamModel,
            req.InputTokens, req.OutputTokens, req.CacheCreateTokens,
            req.CacheReadTokens, req.DurationMs, req.FirstTokenMs,
            req.Stream, req.ClientDisconnect));

        // Update API key quota
        var apiKeyGrain = _cluster.GetGrain<IApiKeyGrain>(req.ApiKeyHash);
        await apiKeyGrain.AddUsage(ComputeCost(req));
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

    private static decimal ComputeCost(UsageReportRequest req)
    {
        // Simplified cost model — in production: per-model pricing table
        var inputCost = req.InputTokens * 0.000003m;
        var outputCost = req.OutputTokens * 0.000015m;
        return (inputCost + outputCost) * (decimal)req.RateMultiplier;
    }
}

// Request/Response DTOs for the RPC bridge
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
