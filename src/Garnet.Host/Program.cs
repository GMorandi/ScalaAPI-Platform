using Sub2Api.Host.Services;

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
builder.Services.AddSingleton<IGarnetService>(new EmbeddedGarnetService(garnetSocket));
builder.Services.AddHostedService(sp => (EmbeddedGarnetService)sp.GetRequiredService<IGarnetService>());

// Cap'n Proto RPC Server (dispatch service)
builder.Services.AddHostedService<CapnpRpcHostedService>();

// Garnet write-through service (used by grains)
builder.Services.AddSingleton<GarnetWriteThroughService>();

// Dispatch service (bridges Cap'n Proto RPC to Orleans grains)
builder.Services.AddSingleton<DispatchService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
