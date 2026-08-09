using System.Text.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using ScalaAPI.Provider.Mock;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8081");
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", provider = "scalaapi-mock" }));

app.MapGet("/oauth/authorize", (HttpRequest request) =>
{
    if (!string.Equals(request.Query["response_type"], "code", StringComparison.Ordinal)
        || !string.Equals(request.Query["client_id"], "mock-client", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(request.Query["redirect_uri"])
        || string.IsNullOrWhiteSpace(request.Query["state"])
        || string.IsNullOrWhiteSpace(request.Query["code_challenge"]))
        return Results.BadRequest(new { error = "invalid_request" });

    var redirectUri = request.Query["redirect_uri"].ToString();
    if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect)
        || (redirect.Scheme != Uri.UriSchemeHttp && redirect.Scheme != Uri.UriSchemeHttps))
        return Results.BadRequest(new { error = "invalid_redirect_uri" });

    var code = MockOAuthAuthorizationCode.Issue("mock-client", redirectUri,
        request.Query["code_challenge"].ToString());
    var location = QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
    {
        ["code"] = code,
        ["state"] = request.Query["state"].ToString(),
    });
    return Results.Redirect(location);
});

app.MapPost("/oauth/token", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
        return Results.Json(new { error = "invalid_request" }, statusCode: 415);
    var form = await request.ReadFormAsync(cancellationToken);
    var grantType = form["grant_type"].ToString();
    if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
    {
        var accepted = MockOAuthTokenEndpoint.RedeemAuthorizationCode(
            form["code"].ToString(), form["client_id"].ToString(),
            form["client_secret"].ToString(), form["redirect_uri"].ToString(),
            form["code_verifier"].ToString());
        return accepted
            ? Results.Ok(new
            {
                access_token = "mock-oauth-access-v1",
                token_type = "Bearer",
                expires_in = 3600,
            })
            : Results.BadRequest(new { error = "invalid_grant" });
    }

    var outcome = MockOAuthTokenEndpoint.Resolve(
        grantType, form["client_id"].ToString(), form["client_secret"].ToString(),
        form["refresh_token"].ToString());
    switch (outcome.Kind)
    {
        case MockOAuthOutcomeKind.Success:
            return Results.Ok(new
            {
                access_token = $"mock-access-v{outcome.Version}",
                refresh_token = $"mock-refresh-v{outcome.Version}",
                token_type = "Bearer",
                expires_in = 3600,
            });
        case MockOAuthOutcomeKind.Timeout:
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return Results.StatusCode(504);
        case MockOAuthOutcomeKind.Malformed:
            return Results.Text("{not-json", "application/json", statusCode: 200);
        case MockOAuthOutcomeKind.Oversized:
            return Results.Text(new string('x', 70 * 1024), "application/json", statusCode: 200);
        default:
            return Results.BadRequest(new { error = outcome.Error });
    }
});

app.MapGet("/oauth/user", (HttpRequest request) =>
{
    if (!string.Equals(request.Headers.Authorization.ToString(),
        "Bearer mock-oauth-access-v1", StringComparison.Ordinal))
        return Results.Unauthorized();
    return Results.Ok(new { id = "mock-oauth-user", email = "oauth-user@example.test" });
});

app.MapGet("/oauth/user/emails", (HttpRequest request) =>
{
    if (!string.Equals(request.Headers.Authorization.ToString(),
        "Bearer mock-oauth-access-v1", StringComparison.Ordinal))
        return Results.Unauthorized();
    return Results.Ok(new[]
    {
        new { email = "oauth-user@example.test", primary = true, verified = true }
    });
});

app.MapGet("/v1/models", (HttpRequest request) =>
{
    var scenario = request.Query["mock_scenario"].ToString();
    if (scenario == "malformed")
        return Results.Text("{not-json", "application/json", statusCode: 200);
    if (scenario == "duplicate")
        return Results.Ok(new
        {
            @object = "list",
            data = new[]
            {
                new { id = "gpt-4o", @object = "model", created = 1_700_000_000L, owned_by = "scalaapi-provider-mock" },
                new { id = "gpt-4o", @object = "model", created = 1_700_000_000L, owned_by = "scalaapi-provider-mock" },
            }
        });
    return Results.Ok(new
    {
        @object = "list",
        data = new[]
        {
            new { id = "gpt-4o", @object = "model", created = 1_700_000_000L, owned_by = "scalaapi-provider-mock" },
            new { id = "text-embedding-3-small", @object = "model", created = 1_700_000_001L, owned_by = "scalaapi-provider-mock" },
            new { id = "mock-image-1", @object = "model", created = 1_700_000_002L, owned_by = "scalaapi-provider-mock" },
            new { id = "mock-video-1", @object = "model", created = 1_700_000_003L, owned_by = "scalaapi-provider-mock" },
        }
    });
});

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

app.MapGet("/v1/responses", async (HttpContext context, CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[64 * 1024];
    var firstFrame = new StringBuilder();
    WebSocketReceiveResult received;
    do
    {
        received = await socket.ReceiveAsync(buffer, cancellationToken);
        if (received.MessageType == WebSocketMessageType.Close)
        {
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", cancellationToken);
            return;
        }
        if (received.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
            continue;
        firstFrame.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
    } while (!received.EndOfMessage);

    var model = "gpt-4o";
    try
    {
        using var document = JsonDocument.Parse(firstFrame.ToString());
        var root = document.RootElement;
        if (root.TryGetProperty("session", out var session)
            && session.ValueKind == JsonValueKind.Object
            && session.TryGetProperty("model", out var sessionModel)
            && sessionModel.ValueKind == JsonValueKind.String)
            model = sessionModel.GetString() ?? model;
    }
    catch (JsonException)
    {
        await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
            "first realtime event must be JSON", cancellationToken);
        return;
    }

    async Task SendAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text,
            endOfMessage: true, cancellationToken);
    }

    await SendAsync(new
    {
        type = "session.created",
        session = new { id = "mock-realtime-session", model }
    });
    await SendAsync(new
    {
        type = "response.done",
        response = new
        {
            id = "mock-realtime-response",
            status = "completed",
            usage = new { input_tokens = 7, output_tokens = 5 }
        }
    });

    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
        received = await socket.ReceiveAsync(buffer, cancellationToken);
        if (received.MessageType == WebSocketMessageType.Close)
        {
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", cancellationToken);
            break;
        }
    }
});

app.MapPost("/v1/embeddings", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    var model = MockProviderHelpers.Model(root, "text-embedding-3-small");
    var scenario = MockProviderHelpers.Scenario(context, root).ToLowerInvariant();
    if (scenario == "429")
        return Results.Json(new { error = new { code = "mock_rate_limited" } }, statusCode: 429);
    if (scenario == "500")
        return Results.Json(new { error = new { code = "mock_upstream_failure" } }, statusCode: 500);
    if (scenario == "malformed")
        return Results.Text("{not-json", "application/json", statusCode: 200);

    var inputCount = MockProviderHelpers.EmbeddingInputCount(root);
    var dimensions = MockProviderHelpers.EmbeddingDimensions(root);
    var encoding = MockProviderHelpers.EmbeddingEncoding(root);
    if (inputCount is < 1 or > 2048 || dimensions is < 1 or > 8192
        || (encoding != "float" && encoding != "base64"))
        return Results.Json(new { error = new { code = "invalid_embedding_request" } }, statusCode: 400);
    var responseDimensions = scenario == "invalid_response" ? dimensions + 1 : dimensions;
    var data = Enumerable.Range(0, inputCount).Select(index => new
    {
        @object = "embedding",
        index,
        embedding = encoding == "base64"
            ? (object)MockProviderHelpers.EmbeddingBase64(index, responseDimensions)
            : MockProviderHelpers.EmbeddingValues(index, responseDimensions)
    }).ToArray();
    var inputTokens = MockProviderHelpers.EstimateEmbeddingInputTokens(root);
    return Results.Ok(new
    {
        @object = "list",
        data,
        model,
        usage = new { prompt_tokens = inputTokens, total_tokens = inputTokens }
    });
});

app.MapPost("/v1/messages/count_tokens", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    if (body.RootElement.TryGetProperty("mock_scenario", out var scenario)
        && scenario.ValueKind == JsonValueKind.String)
    {
        if (scenario.GetString() == "malformed")
            return Results.Text("{not-json", "application/json", statusCode: 200);
        if (scenario.GetString() == "invalid")
            return Results.Ok(new { input_tokens = 0 });
    }
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
    var authorization = context.Request.Headers.Authorization.FirstOrDefault();
    if (authorization?.StartsWith("Bearer mock-access-", StringComparison.Ordinal) == true
        && !MockOAuthTokenEndpoint.IsAcceptedAccessHeader(authorization))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new { code = "mock_access_token_expired" }
        }, cancellationToken);
        return;
    }
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
        case "disconnect_after_usage":
            if (stream)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"partial\"}},\"finish_reason\":null}}]}}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":7,\"completion_tokens\":5,\"total_tokens\":12}}}}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                // End the HTTP body without sending the terminal [DONE]
                // marker. The usage frame is durable evidence even though
                // the Provider stream is truncated at EOF.
                return;
            }
            context.Response.StatusCode = StatusCodes.Status200OK;
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
                context.Abort();
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

public partial class Program;
