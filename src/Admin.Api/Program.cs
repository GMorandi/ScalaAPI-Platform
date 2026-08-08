using Microsoft.AspNetCore.Authentication.JwtBearer;
using Orleans.Configuration;
using SqlSugar;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Admin.Endpoints;
using ScalaAPI.Data.Repositories;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;

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
        opts.ClusterId = "platform";
        opts.ServiceId = "platform-control-plane";
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
builder.Services.AddSingleton<AuthSessionService>();
builder.Services.AddSingleton<PasswordResetService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = jwtService.GetValidationParameters();
        opts.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var sessionId = context.Principal?.FindFirst("sid")?.Value;
                var sessions = context.HttpContext.RequestServices.GetRequiredService<AuthSessionService>();
                if (string.IsNullOrWhiteSpace(sessionId)
                    || !await sessions.IsActiveAsync(sessionId, context.HttpContext.RequestAborted))
                    context.Fail("session_revoked");
            }
        };
    });
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

app.MapAuthEndpoints();
app.MapDashboardEndpoints();
app.MapAccountEndpoints();
app.MapGroupEndpoints();
app.MapUserEndpoints();
app.MapApiKeyEndpoints();
app.MapConfigEndpoints();
app.MapUsageEndpoints();
app.MapSeedEndpoints();
app.MapUserAuthEndpoints();
app.MapPlatformEndpoints();

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
    var registry = scope.ServiceProvider.GetRequiredService<ListingRepository>();
    var normalized = username.Trim().ToLowerInvariant();
    var existing = await db.Queryable<ScalaAPI.Data.Entities.UserAccountEntity>()
        .Where(x => x.Email == normalized).FirstAsync();
    if (existing is not null)
    {
        await registry.RegisterInteger("user", existing.Id);
        return;
    }

    var account = new ScalaAPI.Data.Entities.UserAccountEntity
    {
        Email = normalized,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        Role = "admin",
        Status = "active",
        CreatedAt = DateTime.UtcNow,
    };
    await db.Insertable(account).ExecuteCommandAsync();
    account.Id = Convert.ToInt64(await db.Ado.GetScalarAsync(
        "SELECT id FROM user_accounts WHERE email = @email",
        new SqlSugar.SugarParameter("@email", normalized)));
    await registry.RegisterInteger("user", account.Id);
}
