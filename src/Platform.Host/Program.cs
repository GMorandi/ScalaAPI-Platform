using ScalaAPI.Host.Services;
using ScalaAPI.Data.Accounting;
using Npgsql;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

var pgConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

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
builder.Services.AddSingleton<GarnetProjectionRebuildService>();
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<ScalaAPI.Grains.Interfaces.ICredentialProtector>(sp =>
    sp.GetRequiredService<CredentialProtector>());

// Invalidation publisher (bumps Garnet version on auth data changes)
builder.Services.AddSingleton<ScalaAPI.Host.Services.InvalidationService>();
builder.Services.AddSingleton<ScalaAPI.Grains.Interfaces.IInvalidationService>(sp =>
    sp.GetRequiredService<ScalaAPI.Host.Services.InvalidationService>());

// Dispatch service (bridges Cap'n Proto RPC to Orleans grains)
builder.Services.AddSingleton<ModelPricingService>();
builder.Services.AddHostedService<PricingRefreshHostedService>();
builder.Services.AddSingleton(NpgsqlDataSource.Create(pgConnection));
builder.Services.AddSingleton<AccountingStore>();
builder.Services.AddSingleton<IAccountingProjectionRepairer, OrleansAccountingProjectionRepairer>();
builder.Services.AddSingleton<AccountingReconciliationService>();
builder.Services.AddHttpClient<ObjectStorageClient>();
builder.Services.AddSingleton<RequestLeaseStore>();
builder.Services.AddSingleton<MediaOperationStore>();
builder.Services.AddSingleton<DispatchService>();
builder.Services.AddHostedService<LeaseOutboxHostedService>();
builder.Services.AddHostedService<AccountingProjectionHostedService>();
builder.Services.AddHostedService<AccountingReconciliationHostedService>();
builder.Services.AddHostedService<MediaOperationHostedService>();

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
