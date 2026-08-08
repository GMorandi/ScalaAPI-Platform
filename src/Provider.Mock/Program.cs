using System.Text.Json;
using ScalaAPI.Provider.Mock;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8081");
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", provider = "scalaapi-mock" }));

app.MapGet("/v1/models", () => Results.Ok(new
{
    @object = "list",
    data = new[]
    {
        new { id = "gpt-4o", @object = "model", created = 1_700_000_000L, owned_by = "scalaapi-provider-mock" },
        new { id = "text-embedding-3-small", @object = "model", created = 1_700_000_001L, owned_by = "scalaapi-provider-mock" },
        new { id = "mock-image-1", @object = "model", created = 1_700_000_002L, owned_by = "scalaapi-provider-mock" },
        new { id = "mock-video-1", @object = "model", created = 1_700_000_003L, owned_by = "scalaapi-provider-mock" },
    }
}));

app.MapGet("/v1beta/models", () => Results.Ok(new
{
    models = new[]
    {
        new
        {
            name = "models/gemini-2.0-flash",
            displayName = "Gemini 2.0 Flash",
            description = "Deterministic ScalaAPI provider fixture",
            inputTokenLimit = 1_000_000,
            outputTokenLimit = 8_192,
            supportedGenerationMethods = new[] { "generateContent", "streamGenerateContent" }
        }
    }
}));

app.MapGet("/v1beta/models/{model}", (string model) => Results.Ok(new
{
    name = $"models/{model}",
    displayName = model,
    description = "Deterministic ScalaAPI provider fixture",
    inputTokenLimit = 1_000_000,
    outputTokenLimit = 8_192,
    supportedGenerationMethods = new[] { "generateContent", "streamGenerateContent" }
}));

app.MapPost("/v1/embeddings", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var model = MockProviderHelpers.Model(body.RootElement, "text-embedding-3-small");
    var inputTokens = MockProviderHelpers.EstimateInputTokens(body.RootElement);
    return Results.Ok(new
    {
        @object = "list",
        data = new[] { new { @object = "embedding", index = 0, embedding = new[] { 0.125, 0.25, 0.5, 0.75 } } },
        model,
        usage = new { prompt_tokens = inputTokens, total_tokens = inputTokens }
    });
});

app.MapPost("/v1/messages/count_tokens", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    return Results.Ok(new { input_tokens = MockProviderHelpers.EstimateInputTokens(body.RootElement) });
});

app.MapPost("/v1/messages", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    var model = MockProviderHelpers.Model(root, "claude-3-5-sonnet");
    var inputTokens = MockProviderHelpers.EstimateInputTokens(root);
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? MockProviderHelpers.Id("msg");
    var stream = root.TryGetProperty("stream", out var streamValue)
        && streamValue.ValueKind == JsonValueKind.True;
    var scenario = root.TryGetProperty("mock_scenario", out var scenarioValue)
        ? scenarioValue.GetString() ?? "success"
        : "success";
    if (scenario == "success"
        && root.TryGetProperty("metadata", out var metadata)
        && metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty("user_id", out var userId)
        && userId.GetString() == "scalaapi-json-stream")
        scenario = "json_stream";
    if (stream && !scenario.Equals("json_stream", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var events = new (string Name, object Payload)[]
        {
            ("message_start", new
            {
                type = "message_start",
                message = new
                {
                    id = requestId,
                    type = "message",
                    role = "assistant",
                    model,
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                    usage = new { input_tokens = inputTokens, output_tokens = 0 }
                }
            }),
            ("content_block_start", new
            {
                type = "content_block_start",
                index = 0,
                content_block = new { type = "text", text = "" }
            }),
            ("content_block_delta", new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "text_delta", text = "mock response" }
            }),
            ("content_block_stop", new { type = "content_block_stop", index = 0 }),
            ("message_delta", new
            {
                type = "message_delta",
                delta = new { stop_reason = "end_turn", stop_sequence = (string?)null },
                usage = new { output_tokens = 5 }
            }),
            ("message_stop", new { type = "message_stop" }),
        };
        foreach (var item in events)
        {
            await context.Response.WriteAsync(
                $"event: {item.Name}\ndata: {JsonSerializer.Serialize(item.Payload)}\n\n",
                cancellationToken);
        }
        return Results.Empty;
    }
    return Results.Ok(new
    {
        id = requestId,
        type = "message",
        role = "assistant",
        model,
        content = new[] { new { type = "text", text = "mock response" } },
        stop_reason = "end_turn",
        stop_sequence = (string?)null,
        usage = new { input_tokens = inputTokens, output_tokens = 5 }
    });
});

app.MapPost("/v1/responses", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    var model = MockProviderHelpers.Model(root);
    var inputTokens = MockProviderHelpers.EstimateInputTokens(root);
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? MockProviderHelpers.Id("resp");
    return Results.Ok(new
    {
        id = requestId,
        @object = "response",
        status = "completed",
        model,
        output_text = "mock response",
        output = new[]
        {
            new
            {
                type = "message",
                role = "assistant",
                content = new[] { new { type = "output_text", text = "mock response" } }
            }
        },
        usage = new { input_tokens = inputTokens, output_tokens = 5, total_tokens = inputTokens + 5 }
    });
});

app.MapPost("/v1beta/models/{model}:generateContent", async (
    string model, HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var inputTokens = MockProviderHelpers.EstimateInputTokens(body.RootElement);
    return Results.Ok(new
    {
        candidates = new[]
        {
            new
            {
                content = new { role = "model", parts = new[] { new { text = "mock response" } } },
                finishReason = "STOP",
                index = 0
            }
        },
        usageMetadata = new
        {
            promptTokenCount = inputTokens,
            candidatesTokenCount = 5,
            totalTokenCount = inputTokens + 5
        },
        modelVersion = model
    });
});

app.MapPost("/v1beta/models/{model}:streamGenerateContent", async (
    string model, HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var inputTokens = MockProviderHelpers.EstimateInputTokens(body.RootElement);
    context.Response.ContentType = "text/event-stream";
    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = "mock response" } } }, finishReason = "STOP" } },
        usageMetadata = new { promptTokenCount = inputTokens, candidatesTokenCount = 5, totalTokenCount = inputTokens + 5 },
        modelVersion = model
    })}\n\n", cancellationToken);
});

app.MapPost("/v1/images/generations", (HttpContext context) =>
{
    var id = MockProviderHelpers.Id("image");
    return Results.Ok(new
    {
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        data = new[] { new { url = MockProviderHelpers.OutputUrl(id), revised_prompt = "mock image" } },
        size = "1024x1024"
    });
});

app.MapPost("/v1/images/edits", (HttpContext context) =>
{
    var id = MockProviderHelpers.Id("image-edit");
    return Results.Ok(new
    {
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        data = new[] { new { url = MockProviderHelpers.OutputUrl(id), revised_prompt = "mock image edit" } },
        size = "1024x1024"
    });
});

app.MapPost("/v1/images/generations/async", () => Results.Accepted(value: new
{
    id = MockProviderHelpers.Id("image-task"),
    status = "pending",
    progress = 0
}));

app.MapPost("/v1/images/edits/async", () => Results.Accepted(value: new
{
    id = MockProviderHelpers.Id("image-edit-task"),
    status = "pending",
    progress = 0
}));

app.MapPost("/v1/images/batches", () => Results.Accepted(value: new
{
    id = MockProviderHelpers.Id("image-batch"),
    status = "pending",
    progress = 0
}));

app.MapGet("/v1/images/batches/models", () => Results.Ok(new
{
    @object = "list",
    data = new[] { new { id = "mock-image-1", @object = "model", owned_by = "scalaapi-provider-mock" } }
}));

app.MapGet("/v1/images/tasks/{taskId}", (string taskId) => Results.Ok(new
{
    id = taskId,
    status = "succeeded",
    progress = 100,
    output_url = MockProviderHelpers.OutputUrl(taskId),
    content_type = "image/png",
    data = new[] { new { url = MockProviderHelpers.OutputUrl(taskId) } },
    size = "1024x1024"
}));

app.MapGet("/v1/images/batches/{batchId}", (string batchId) => Results.Ok(new
{
    id = batchId,
    status = "succeeded",
    progress = 100,
    output_url = MockProviderHelpers.OutputUrl(batchId),
    content_type = "application/json",
    data = new[] { new { custom_id = "mock-1", url = MockProviderHelpers.OutputUrl(batchId) } }
}));

app.MapPost("/v1/videos/generations", () => Results.Accepted(value: new
{
    id = MockProviderHelpers.Id("video"),
    status = "pending",
    progress = 0
}));

app.MapPost("/v1/videos/edits", () => Results.Accepted(value: new
{
    id = MockProviderHelpers.Id("video-edit"),
    status = "pending",
    progress = 0
}));

app.MapPost("/v1/videos/extensions", () => Results.Accepted(value: new
{
    id = MockProviderHelpers.Id("video-extension"),
    status = "pending",
    progress = 0
}));

app.MapGet("/v1/videos/{videoId}", (string videoId) => Results.Ok(new
{
    id = videoId,
    status = "succeeded",
    progress = 100,
    output_url = MockProviderHelpers.OutputUrl(videoId),
    content_type = "video/mp4",
    resolution = "1280x720",
    duration = 4
}));

app.MapGet("/v1/mock-output/{outputId}", (string outputId) =>
{
    var bytes = System.Text.Encoding.UTF8.GetBytes($"scalaapi-provider-mock:{outputId}\n");
    return Results.File(bytes, outputId.StartsWith("video", StringComparison.OrdinalIgnoreCase)
        ? "video/mp4" : "image/png");
});

app.MapPost("/v1/chat/completions", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    var root = body.RootElement;
    var model = root.TryGetProperty("model", out var modelValue)
        ? modelValue.GetString() ?? "mock-model"
        : "mock-model";
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? $"mock-{Guid.NewGuid():N}";
    var scenario = MockProviderHelpers.Scenario(context, root);
    var stream = root.TryGetProperty("stream", out var streamValue)
        && streamValue.ValueKind == JsonValueKind.True;

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
        case "invalid_content_type":
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = stream ? "application/json" : "text/plain";
            if (stream)
            {
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"wrong media type\"}},\"finish_reason\":\"stop\"}}]}}\n\n",
                    cancellationToken);
            }
            else
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    id = requestId,
                    model,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "wrong media type" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 7, completion_tokens = 5, total_tokens = 12 }
                }), cancellationToken);
            }
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
            if (stream)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"partial\"}},\"finish_reason\":null}}]}}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                context.Abort();
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync($"{{\"id\":\"{requestId}\",\"choices\":[", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            context.Abort();
            return;
        case "disconnect_before_output":
            if (stream)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                // A zero-length response closes deterministically without a
                // terminal SSE event; the Gateway must retain the hold.
                context.Response.ContentLength = 0;
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Abort();
            return;
        case "client_disconnect":
            if (stream)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"first\"}},\"finish_reason\":null}}]}}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                // Give a short-lived client time to close after the first event.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                for (var index = 0; index < 32; index++)
                {
                    await context.Response.WriteAsync(
                        $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"continued-{index}\"}},\"finish_reason\":null}}]}}\n\n",
                        cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Abort();
            return;
        case "malformed_usage":
            if (stream)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":-1,\"completion_tokens\":\"invalid\"}}}}\n\ndata: [DONE]\n\n",
                    cancellationToken);
                return;
            }
            await context.Response.WriteAsJsonAsync(new
            {
                id = requestId,
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "mock response" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = -1, completion_tokens = "invalid", total_tokens = 0 }
            }, cancellationToken);
            return;
    }

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
