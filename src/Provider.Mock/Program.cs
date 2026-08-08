using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8081");
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", provider = "scalaapi-mock" }));

app.MapPost("/v1/chat/completions", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    var root = body.RootElement;
    var model = root.TryGetProperty("model", out var modelValue)
        ? modelValue.GetString() ?? "mock-model"
        : "mock-model";
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? $"mock-{Guid.NewGuid():N}";
    var scenario = context.Request.Headers["X-Provider-Scenario"].FirstOrDefault()
        ?? context.Request.Query["scenario"].FirstOrDefault()
        ?? (root.TryGetProperty("mock_scenario", out var scenarioValue)
            ? scenarioValue.GetString()
            : null)
        ?? "success";

    switch (scenario.ToLowerInvariant())
    {
        case "429":
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "mock_rate_limited" } }, cancellationToken);
            return;
        case "500":
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "mock_upstream_failure" } }, cancellationToken);
            return;
        case "delay":
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            break;
        case "timeout":
            // Keep the request open until the Gateway cancellation deadline.
            // The token lets shutdown/client cancellation release the handler.
            await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
            return;
        case "disconnect":
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync($"{{\"id\":\"{requestId}\",\"choices\":[", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            context.Abort();
            return;
        case "malformed_usage":
            await context.Response.WriteAsJsonAsync(new
            {
                id = requestId,
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "mock response" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = -1, completion_tokens = "invalid", total_tokens = 0 }
            }, cancellationToken);
            return;
    }

    var stream = root.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind == JsonValueKind.True;
    var usage = new { prompt_tokens = 7, completion_tokens = 5, total_tokens = 12 };
    if (stream || scenario.Equals("sse", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync($"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":\"mock response\"}},\"finish_reason\":null}}]}}\n\n", cancellationToken);
        await context.Response.WriteAsync($"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{JsonSerializer.Serialize(usage)}}}\n\n", cancellationToken);
        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        return;
    }

    await context.Response.WriteAsJsonAsync(new
    {
        id = requestId,
        @object = "chat.completion",
        model,
        choices = new[] { new { index = 0, message = new { role = "assistant", content = "mock response" }, finish_reason = "stop" } },
        usage
    }, cancellationToken);
});

app.MapGet("/v1/requests/{requestId}", (string requestId) => Results.Ok(new
{
    request_id = requestId,
    usage = new { prompt_tokens = 7, completion_tokens = 5, total_tokens = 12 }
}));

app.Run();
