using Sub2Api.Host.Services;
using Sub2Api.Data.Migration;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var pgConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");
var garnetSocket = builder.Configuration["Garnet:SocketPath"]
    ?? "/var/run/sub2api/garnet.sock";

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
        opts.ClusterId = "sub2api";
        opts.ServiceId = "sub2api-platform";
    });

    silo.Configure<Orleans.Configuration.EndpointOptions>(opts =>
    {
        opts.AdvertisedIPAddress = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .First(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        opts.SiloPort = 11111;
        opts.GatewayPort = 30000;
    });
});

// Embedded Garnet (in-memory hot cache, RESP on UDS for C++ gateway reads)
builder.Services.AddSingleton<EmbeddedGarnetService>(sp =>
    new EmbeddedGarnetService(garnetSocket,
        sp.GetRequiredService<ILogger<EmbeddedGarnetService>>()));
builder.Services.AddSingleton<IGarnetService>(sp => sp.GetRequiredService<EmbeddedGarnetService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmbeddedGarnetService>());

// Cap'n Proto RPC Server (dispatch service)
builder.Services.AddSingleton<CapnpRpcHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CapnpRpcHostedService>());

// Garnet write-through service (used by grains)
builder.Services.AddSingleton<GarnetWriteThroughService>();
builder.Services.AddSingleton<AuthProjectionCache>();
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<Sub2Api.Grains.Interfaces.ICredentialProtector>(sp =>
    sp.GetRequiredService<CredentialProtector>());

// Invalidation publisher (bumps Garnet version on auth data changes)
builder.Services.AddSingleton<Sub2Api.Host.Services.InvalidationService>();
builder.Services.AddSingleton<Sub2Api.Grains.Interfaces.IInvalidationService>(sp =>
    sp.GetRequiredService<Sub2Api.Host.Services.InvalidationService>());

// Dispatch service (bridges Cap'n Proto RPC to Orleans grains)
builder.Services.AddSingleton<ModelPricingService>();
builder.Services.AddSingleton(NpgsqlDataSource.Create(pgConnection));
builder.Services.AddSingleton<CdcInboxStore>();
builder.Services.AddSingleton<CdcCredentialStore>();
builder.Services.AddSingleton<MigrationFenceStore>();
builder.Services.AddSingleton<MigrationWriteGate>();
builder.Services.AddSingleton<CdcGrainApplier>();
builder.Services.AddSingleton<RequestLeaseStore>();
builder.Services.AddSingleton<MediaOperationStore>();
builder.Services.AddSingleton<DispatchService>();
builder.Services.AddHostedService<LeaseOutboxHostedService>();
builder.Services.AddHostedService<MediaOperationHostedService>();
builder.Services.AddHostedService<CdcConsumerHostedService>();

var app = builder.Build();

app.Services.GetRequiredService<CredentialProtector>();

app.MapGet("/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/ready", async (NpgsqlDataSource db, CapnpRpcHostedService rpc, CancellationToken ct) =>
{
    if (!rpc.IsListening) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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
app.MapGet("/metrics", async (NpgsqlDataSource db, MigrationWriteGate writeGate, CancellationToken ct) =>
{
    await using var command = db.CreateCommand("""
        SELECT
          (SELECT count(*) FROM request_leases WHERE status = 'active'),
          (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL),
          (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL AND attempts > 0),
          (SELECT count(*) FROM cdc_inbox WHERE status IN ('pending', 'failed')),
          (SELECT count(*) FROM cdc_inbox WHERE status = 'dead_letter'),
          (SELECT count(*) FROM cdc_rejected_messages),
          (SELECT count(*) FROM media_operations WHERE status IN ('pending', 'running')),
          (SELECT count(*) FROM media_operations WHERE status IN ('pending', 'running') AND expires_at <= now())
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
        # TYPE platform_cdc_pending gauge
        platform_cdc_pending {reader.GetInt64(3)}
        # TYPE platform_cdc_dead_letters gauge
        platform_cdc_dead_letters {reader.GetInt64(4)}
        # TYPE platform_cdc_rejected_messages counter
        platform_cdc_rejected_messages {reader.GetInt64(5)}
        # TYPE platform_media_operation_backlog gauge
        platform_media_operation_backlog {reader.GetInt64(6)}
        # TYPE platform_media_operation_overdue gauge
        platform_media_operation_overdue {reader.GetInt64(7)}
        # TYPE platform_migration_fence_rejections counter
        platform_migration_fence_rejections {writeGate.RejectionCount}
        """;
    return Results.Text(body, "text/plain; version=0.0.4");
});
app.MapGet("/migration/fence", async (MigrationFenceStore store, CancellationToken ct) =>
    Results.Ok(await store.GetAsync(ct)));
app.MapGet("/migration/health", async (NpgsqlDataSource db, MigrationFenceStore fence,
    MigrationWriteGate writeGate, CancellationToken ct) =>
{
    await using var command = db.CreateCommand("""
        SELECT
          (SELECT count(*) FROM cdc_inbox WHERE status IN ('pending', 'failed')),
          (SELECT count(*) FROM cdc_inbox WHERE status = 'dead_letter'),
          (SELECT min(received_at) FROM cdc_inbox WHERE status IN ('pending', 'failed')),
          (SELECT max(updated_at) FROM cdc_checkpoints),
          (SELECT count(*) FROM cdc_rejected_messages)
        """);
    await using var reader = await command.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    return Results.Ok(new
    {
        fence = await fence.GetAsync(ct),
        pending = reader.GetInt64(0),
        deadLetters = reader.GetInt64(1),
        oldestPendingAt = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2),
        checkpointUpdatedAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3),
        rejectedMessages = reader.GetInt64(4),
        fenceRejections = writeGate.RejectionCount,
        lagSeconds = reader.IsDBNull(2) ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - reader.GetFieldValue<DateTimeOffset>(2)).TotalSeconds)
    });
});

app.Run();
