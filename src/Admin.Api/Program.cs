using Microsoft.AspNetCore.Authentication.JwtBearer;
using Orleans.Configuration;
using SqlSugar;
using Sub2Api.Admin.Auth;
using Sub2Api.Admin.Data;
using Sub2Api.Admin.Endpoints;
using Sub2Api.Data.Repositories;
using Sub2Api.Data.Migration;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var pgConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

builder.UseOrleansClient(client =>
{
    client.UseAdoNetClustering(opts =>
    {
        opts.ConnectionString = pgConnection;
        opts.Invariant = "Npgsql";
    });
    client.Configure<ClusterOptions>(opts =>
    {
        opts.ClusterId = "sub2api";
        opts.ServiceId = "sub2api-platform";
    });
});

var jwtService = new JwtService(builder.Configuration);
builder.Services.AddSingleton(jwtService);
builder.Services.AddSingleton<SecretProtector>();

builder.Services.AddScoped<ISqlSugarClient>(_ => new SqlSugarClient(new ConnectionConfig
{
    ConnectionString = pgConnection,
    DbType = DbType.PostgreSQL,
    IsAutoCloseConnection = true,
    InitKeyType = InitKeyType.Attribute,
}));
builder.Services.AddScoped<ListingRepository>();
builder.Services.AddScoped<IUsageLogRepository, UsageLogRepository>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(NpgsqlDataSource.Create(pgConnection));
builder.Services.AddSingleton<CdcInboxStore>();
builder.Services.AddSingleton<MigrationFenceStore>();
builder.Services.AddSingleton<MigrationWriteGate>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts => opts.TokenValidationParameters = jwtService.GetValidationParameters());
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireAuthenticatedUser().RequireRole("admin"));
    options.AddPolicy("UserOnly", policy => policy.RequireAuthenticatedUser());
});

builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
              .AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// Resolve eagerly so a missing or malformed encryption key fails before the API listens.
app.Services.GetRequiredService<SecretProtector>();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Business mutations must be fenced. Migration control endpoints and login
// metadata remain available while Sub2API is the legacy write primary.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var path = context.Request.Path;
    var isMutation = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
    var isBusinessPath = path.StartsWithSegments("/admin")
        && !path.StartsWithSegments("/admin/auth")
        && !path.StartsWithSegments("/admin/migration")
        || path.StartsWithSegments("/user")
        || path.Equals("/auth/register")
        || path.Equals("/auth/oauth/callback");
    if (isMutation && isBusinessPath)
    {
        try
        {
            await context.RequestServices.GetRequiredService<MigrationWriteGate>()
                .AssertPlatformPrimaryAsync(context.RequestAborted);
        }
        catch (MigrationWriteRejectedException ex)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "migration_fence",
                message = ex.Message
            }, context.RequestAborted);
            return;
        }
    }
    await next(context);
});

app.MapAuthEndpoints();
app.MapDashboardEndpoints();
app.MapAccountEndpoints();
app.MapGroupEndpoints();
app.MapUserEndpoints();
app.MapApiKeyEndpoints();
app.MapConfigEndpoints();
app.MapUsageEndpoints();
app.MapUserAuthEndpoints();
app.MapPlatformEndpoints();
app.MapMigrationEndpoints();

app.MapGet("/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/ready", async (ISqlSugarClient db) =>
{
    try
    {
        await db.Ado.GetIntAsync("SELECT 1");
        return Results.Ok(new { status = "ready" });
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

await BootstrapAdminAsync(app.Services, builder.Configuration);

app.Run();

static async Task BootstrapAdminAsync(IServiceProvider services, IConfiguration configuration)
{
    var username = configuration["Admin:Username"];
    var password = configuration["Admin:Password"];
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        return;
    if (password.Length < 12)
        throw new InvalidOperationException("Admin:Password must be at least 12 characters");

    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
    var normalized = username.Trim().ToLowerInvariant();
    var existing = await db.Queryable<Sub2Api.Data.Entities.UserAccountEntity>()
        .Where(x => x.Email == normalized).FirstAsync();
    if (existing is not null) return;

    // Startup provisioning is a business write too. During legacy-primary
    // operation the target may be queried, but must not create a second writer.
    try
    {
        await scope.ServiceProvider.GetRequiredService<MigrationWriteGate>()
            .AssertPlatformPrimaryAsync();
    }
    catch (MigrationWriteRejectedException)
    {
        return;
    }

    await db.Insertable(new Sub2Api.Data.Entities.UserAccountEntity
    {
        Email = normalized,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        Role = "admin",
        Status = "active",
        CreatedAt = DateTime.UtcNow,
    }).ExecuteCommandAsync();
}
