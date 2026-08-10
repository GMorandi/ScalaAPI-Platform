using ScalaAPI.ObjectStorage.FaultProxy;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:9002");
builder.Services.AddSingleton<FaultProxyState>();
builder.Services.AddHostedService<ObjectStorageTcpProxy>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/state", (FaultProxyState state) => Results.Ok(state.Snapshot()));
app.MapPost("/faults/clear", (FaultProxyState state) =>
{
    state.Clear();
    return Results.Ok(state.Snapshot());
});
app.MapPost("/faults/arm", (FaultArmRequest request, FaultProxyState state) =>
{
    try
    {
        return Results.Ok(state.Arm(request));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});
app.Run();

public partial class Program;
