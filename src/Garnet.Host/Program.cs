using Sub2Api.Host.Services;
using Sub2Api.Data.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var pgConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Database=sub2api;Username=postgres;Password=postgres";
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
        opts.AdvertisedIPAddress = System.Net.IPAddress.Loopback;
    });
});

// Embedded Garnet (in-memory hot cache, RESP on UDS for C++ gateway reads)
builder.Services.AddSingleton<EmbeddedGarnetService>(sp =>
    new EmbeddedGarnetService(garnetSocket,
        sp.GetRequiredService<ILogger<EmbeddedGarnetService>>()));
builder.Services.AddSingleton<IGarnetService>(sp => sp.GetRequiredService<EmbeddedGarnetService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmbeddedGarnetService>());

// Cap'n Proto RPC Server (dispatch service)
builder.Services.AddHostedService<CapnpRpcHostedService>();

// Garnet write-through service (used by grains)
builder.Services.AddSingleton<GarnetWriteThroughService>();

// Invalidation publisher (bumps Garnet version on auth data changes)
builder.Services.AddSingleton<Sub2Api.Host.Services.InvalidationService>();
builder.Services.AddSingleton<Sub2Api.Grains.Interfaces.IInvalidationService>(sp =>
    sp.GetRequiredService<Sub2Api.Host.Services.InvalidationService>());

// Dispatch service (bridges Cap'n Proto RPC to Orleans grains)
builder.Services.AddSingleton<ModelPricingService>();
builder.Services.AddSingleton<DispatchService>();

// SqlSugar data layer (usage_logs batch writer)
builder.Services.AddSqlSugarData(pgConnection);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
