using ScalaAPI.Host.Services;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Data.Provider;
using Npgsql;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

var pgConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");
var singleSiloRecovery = builder.Configuration.GetValue("Orleans:SingleSiloRecovery", false);

if (singleSiloRecovery)
{
    // A process crash cannot publish Orleans' graceful shutdown status. In
    // the explicit single-silo mode, retire stale membership rows before the
    // replacement silo joins; multi-silo deployments keep normal liveness
    // voting and never run this cleanup.
    await using var recoveryDataSource = NpgsqlDataSource.Create(pgConnection);
    await using var recoveryConnection = await recoveryDataSource.OpenConnectionAsync();
    await using var recoveryCommand = recoveryConnection.CreateCommand();
    recoveryCommand.CommandText = """
        UPDATE OrleansMembershipTable
        SET Status = 6, SuspectTimes = NULL
        WHERE DeploymentId = 'platform' AND Status > 0 AND Status < 6;
        UPDATE OrleansMembershipVersionTable
        SET Timestamp = now(), Version = Version + 1
        WHERE DeploymentId = 'platform';
        """;
    await recoveryCommand.ExecuteNonQueryAsync();
}

// Orleans Silo
builder.UseOrleans(silo =>
{
    silo.UseAdoNetClustering(opts =>
    {
        opts.ConnectionString = pgConnection;
        opts.Invariant = "Npgsql";
    });

    silo.AddAdoNetGrainStorage("postgres", opts =>
    {
        opts.ConnectionString = pgConnection;
        opts.Invariant = "Npgsql";
    });

    silo.UseAdoNetReminderService(opts =>
    {
        opts.ConnectionString = pgConnection;
        opts.Invariant = "Npgsql";
    });

    silo.AddMemoryGrainStorage("PubSubStore");
    silo.AddMemoryStreams("InvalidationStream");

    silo.Configure<Orleans.Configuration.ClusterOptions>(opts =>
    {
        opts.ClusterId = "platform";
        opts.ServiceId = "platform-control-plane";
    });

    // A single development silo has no peer available to cast a second death
    // vote after a process crash. Keep the production defaults for multi-silo
    // deployments, while making the explicit single-silo mode recoverable.
    silo.Configure<Orleans.Configuration.ClusterMembershipOptions>(opts =>
    {
        if (!singleSiloRecovery)
        {
            return;
        }

        opts.NumProbedSilos = 1;
        opts.NumMissedProbesLimit = 1;
        opts.NumVotesForDeathDeclaration = 1;
        opts.ProbeTimeout = TimeSpan.FromSeconds(1);
        opts.DeathVoteExpirationTimeout = TimeSpan.FromSeconds(5);
        opts.MaxJoinAttemptTime = TimeSpan.FromMinutes(2);
    });

    silo.Configure<Orleans.Configuration.EndpointOptions>(opts =>
    {
        opts.AdvertisedIPAddress = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .First(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        opts.SiloPort = 11111;
        opts.GatewayPort = 30000;
    });
});

// Garnet is an external cache service. There is deliberately no in-process
// fallback: cache availability is part of readiness and scheduling safety.
builder.Services.AddSingleton<RemoteGarnetService>();
builder.Services.AddSingleton<IGarnetService>(sp => sp.GetRequiredService<RemoteGarnetService>());

// Cap'n Proto RPC Server (dispatch service)
builder.Services.AddSingleton<CapnpRpcHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CapnpRpcHostedService>());

// Garnet write-through service (used by grains)
builder.Services.AddSingleton<GarnetWriteThroughService>();
builder.Services.AddSingleton<AuthProjectionCache>();
builder.Services.AddSingleton<GarnetPolicyRevisionRebuildService>();
builder.Services.AddSingleton<GarnetProjectionRebuildService>();
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<ScalaAPI.Grains.Interfaces.ICredentialProtector>(sp =>
    sp.GetRequiredService<CredentialProtector>());
builder.Services.AddHttpClient<ProviderTokenEndpointClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<ProviderCredentialRefreshService>();

// Invalidation publisher (bumps Garnet version on auth data changes)
builder.Services.AddSingleton<ScalaAPI.Host.Services.InvalidationService>();
builder.Services.AddSingleton<ScalaAPI.Grains.Interfaces.IInvalidationService>(sp =>
    sp.GetRequiredService<ScalaAPI.Host.Services.InvalidationService>());

// Dispatch service (bridges Cap'n Proto RPC to Orleans grains)
builder.Services.AddSingleton<ModelPricingService>();
builder.Services.AddHostedService<PricingRefreshHostedService>();
builder.Services.AddHttpClient<ProviderPricingCatalogClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.MaxResponseContentBufferSize = 128 * 1024;
});
builder.Services.AddSingleton<ProviderPricingRefreshService>();
builder.Services.AddHostedService<ProviderPricingRefreshHostedService>();
builder.Services.AddSingleton(NpgsqlDataSource.Create(pgConnection));
builder.Services.AddSingleton<ProviderCredentialRefreshAuditStore>();
builder.Services.AddSingleton<FaultInjection>();
builder.Services.AddSingleton<AccountingStore>();
builder.Services.AddSingleton<IAccountingProjectionRepairer, OrleansAccountingProjectionRepairer>();
builder.Services.AddSingleton<AccountingReconciliationService>();
builder.Services.AddHttpClient<ObjectStorageClient>();
builder.Services.AddSingleton<IMediaObjectStorage>(sp =>
    sp.GetRequiredService<ObjectStorageClient>());
builder.Services.AddSingleton<RequestLeaseStore>();
builder.Services.AddSingleton<MediaOperationStore>();
var classifierEndpoint = builder.Configuration["ContentClassifier:Endpoint"];
var openAiClassifierEndpoint = builder.Configuration["ContentClassifier:OpenAI:Endpoint"];
if (!string.IsNullOrWhiteSpace(openAiClassifierEndpoint))
{
    var allowInsecureOpenAi = builder.Configuration.GetValue(
        "ContentClassifier:OpenAI:AllowInsecure", false);
    if (!Uri.TryCreate(openAiClassifierEndpoint, UriKind.Absolute, out var endpoint)
        || (endpoint.Scheme != Uri.UriSchemeHttps
            && !(allowInsecureOpenAi && endpoint.Scheme == Uri.UriSchemeHttp)))
        throw new InvalidOperationException(
            "ContentClassifier:OpenAI:Endpoint must be an absolute HTTPS URL unless the explicit development AllowInsecure switch is enabled");
    var apiKey = builder.Configuration["ContentClassifier:OpenAI:ApiKey"]?.Trim();
    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 512)
        throw new InvalidOperationException(
            "ContentClassifier:OpenAI:ApiKey is required and bounded");
    var model = (builder.Configuration["ContentClassifier:OpenAI:Model"]
        ?? "omni-moderation-latest").Trim();
    if (model.Length is < 1 or > 128)
        throw new InvalidOperationException(
            "ContentClassifier:OpenAI:Model is invalid");
    var timeoutMs = Math.Clamp(
        builder.Configuration.GetValue("ContentClassifier:OpenAI:TimeoutMs", 750), 100, 5000);
    var options = new OpenAiModerationClientOptions(
        endpoint, apiKey, model, TimeSpan.FromMilliseconds(timeoutMs));
    builder.Services.AddSingleton(options);
    builder.Services.AddHttpClient<OpenAiModerationClassifier>(client =>
    {
        client.Timeout = options.Timeout;
        client.MaxResponseContentBufferSize = OpenAiModerationClientOptions.MaxResponseBytes;
    });
}
if (string.IsNullOrWhiteSpace(classifierEndpoint))
{
    builder.Services.AddSingleton<IContentClassifier, DefaultContentClassifier>();
}
else
{
    if (!Uri.TryCreate(classifierEndpoint, UriKind.Absolute, out var endpoint)
        || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        throw new InvalidOperationException(
            "ContentClassifier:Endpoint must be an absolute HTTP(S) URL");
    var timeoutMs = Math.Clamp(
        builder.Configuration.GetValue("ContentClassifier:TimeoutMs", 750), 100, 5000);
    var options = new ContentClassifierClientOptions(
        endpoint, TimeSpan.FromMilliseconds(timeoutMs));
    builder.Services.AddSingleton(options);
    builder.Services.AddHttpClient<HttpContentClassifier>(client =>
    {
        client.Timeout = options.Timeout;
        client.MaxResponseContentBufferSize = ContentClassifierClientOptions.MaxResponseBytes;
    });
    builder.Services.AddSingleton<IContentClassifier>(sp =>
        sp.GetRequiredService<HttpContentClassifier>());
}
builder.Services.AddSingleton<ContentPolicyService>();
builder.Services.AddSingleton<ContentPolicyPropagationService>();
builder.Services.AddSingleton<DispatchService>();
builder.Services.AddHostedService<LeaseOutboxHostedService>();
builder.Services.AddHostedService<AccountingProjectionHostedService>();
builder.Services.AddHostedService<AccountingReconciliationHostedService>();
builder.Services.AddHostedService<MediaOperationHostedService>();
builder.Services.AddHostedService<MediaObjectReconciliationService>();
builder.Services.AddHostedService<ContentPolicyPropagationHostedService>();

var app = builder.Build();

app.Services.GetRequiredService<CredentialProtector>();

app.MapGet("/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/ready", async (NpgsqlDataSource db, CapnpRpcHostedService rpc,
    RemoteGarnetService garnet, CancellationToken ct) =>
{
    if (!rpc.IsListening || !garnet.Ping())
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    try
    {
        await using var command = db.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapGet("/health", () => Results.Redirect("/ready"));
app.MapPost("/internal/cache/rebuild", async (HttpRequest request,
    IConfiguration configuration, GarnetProjectionRebuildService rebuild,
    CancellationToken ct) =>
{
    var expected = configuration["Internal:CacheRebuildToken"];
    if (string.IsNullOrWhiteSpace(expected)
        || !CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(request.Headers["X-Internal-Token"].ToString())))
        return Results.Unauthorized();

    return Results.Ok(await rebuild.RebuildAsync(ct));
});
app.MapPost("/internal/reconciliation/incidents/{incidentId:long}/resolve", async (
    long incidentId,
    HttpRequest request,
    IConfiguration configuration,
    RequestLeaseStore leases,
    CancellationToken ct) =>
{
    var expected = configuration["Internal:ReconciliationToken"];
    var supplied = request.Headers["X-Internal-Token"].ToString();
    if (string.IsNullOrWhiteSpace(expected)
        || !CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(supplied)))
        return Results.Unauthorized();

    if (!long.TryParse(request.Headers["X-Operator-Id"].ToString(), out var actorId)
        || actorId <= 0)
        return Results.BadRequest(new { error = "operator_identity_required" });
    var idempotencyKey = request.Headers["X-Operator-Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
        return Results.BadRequest(new { error = "operator_idempotency_key_required" });
    var resolution = await request.ReadFromJsonAsync<ReconciliationResolutionRequest>(ct);
    if (resolution is null)
        return Results.BadRequest(new { error = "resolution_payload_required" });

    var result = await leases.ResolveReconciliationAsync(
        incidentId, actorId, idempotencyKey, resolution,
        request.Headers["X-Operator-Ip"].ToString(), ct);
    var response = new
    {
        status = result.Status.ToString().ToLowerInvariant(),
        error_code = result.ErrorCode,
        resolution_id = result.ResolutionId,
        lease_token = result.LeaseToken,
        action = result.Action,
        cost_usd = result.CostUsd,
    };
    return result.Status switch
    {
        ReconciliationResolutionStatus.Applied
            or ReconciliationResolutionStatus.Duplicate => Results.Ok(response),
        ReconciliationResolutionStatus.NotFound => Results.NotFound(response),
        ReconciliationResolutionStatus.Conflict => Results.Conflict(response),
        _ => Results.BadRequest(response),
    };
});
app.MapGet("/metrics", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var command = db.CreateCommand("""
        SELECT
          (SELECT count(*) FROM request_leases
             WHERE status IN ('held', 'forwarded', 'output_started')),
          (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL),
          (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL AND attempts > 0),
          (SELECT count(*) FROM media_operations WHERE status IN ('pending', 'running')),
          (SELECT count(*) FROM media_operations WHERE status IN ('pending', 'running') AND expires_at <= now()),
          (SELECT count(*) FROM accounting_projection_outbox),
          (SELECT count(*) FROM accounting_projection_outbox WHERE attempts > 0),
          (SELECT count(*) FROM accounting_reconciliation_incidents WHERE status = 'open'),
          (SELECT count(*) FROM accounting_reconciliation_incidents
             WHERE status = 'open' AND kind = 'unknown_provider_charge'),
          COALESCE((SELECT extract(epoch FROM now() - min(first_seen_at))::bigint
                    FROM accounting_reconciliation_incidents WHERE status = 'open'), 0),
          COALESCE((SELECT extract(epoch FROM max(completed_at))::bigint
                    FROM ledger_reconciliation_runs WHERE status = 'passed'), 0)
        """);
    await using var reader = await command.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    var body = $"""
        # TYPE platform_active_leases gauge
        platform_active_leases {reader.GetInt64(0)}
        # TYPE platform_usage_outbox_backlog gauge
        platform_usage_outbox_backlog {reader.GetInt64(1)}
        # TYPE platform_settlement_retries gauge
        platform_settlement_retries {reader.GetInt64(2)}
        # TYPE platform_media_operation_backlog gauge
        platform_media_operation_backlog {reader.GetInt64(3)}
        # TYPE platform_media_operation_overdue gauge
        platform_media_operation_overdue {reader.GetInt64(4)}
        # TYPE platform_accounting_projection_backlog gauge
        platform_accounting_projection_backlog {reader.GetInt64(5)}
        # TYPE platform_accounting_projection_retries gauge
        platform_accounting_projection_retries {reader.GetInt64(6)}
        # TYPE platform_reconciliation_open_incidents gauge
        platform_reconciliation_open_incidents {reader.GetInt64(7)}
        # TYPE platform_reconciliation_unknown_charges gauge
        platform_reconciliation_unknown_charges {reader.GetInt64(8)}
        # TYPE platform_reconciliation_oldest_incident_seconds gauge
        platform_reconciliation_oldest_incident_seconds {reader.GetInt64(9)}
        # TYPE platform_reconciliation_last_success_timestamp_seconds gauge
        platform_reconciliation_last_success_timestamp_seconds {reader.GetInt64(10)}
        """;
    return Results.Text(body, "text/plain; version=0.0.4");
});

app.Run();
