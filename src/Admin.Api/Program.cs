using Microsoft.AspNetCore.Authentication.JwtBearer;
using Orleans.Configuration;
using Sub2Api.Admin.Auth;
using Sub2Api.Admin.Data;
using Sub2Api.Admin.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var pgConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Database=sub2api;Username=postgres;Password=postgres";

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
builder.Services.AddSingleton(new ListingRepository(pgConnection));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts => opts.TokenValidationParameters = jwtService.GetValidationParameters());
builder.Services.AddAuthorization();

builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
              .AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

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

app.Run();
