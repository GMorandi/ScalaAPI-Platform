using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using ScalaAPI.Provider.Mock;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8081");
var app = builder.Build();
var responses = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
var responseInputItems = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", provider = "scalaapi-mock" }));

app.MapPost("/v1/payments/checkout", async (HttpRequest request,
    CancellationToken ct) =>
{
    if (!string.Equals(request.Headers.Authorization.ToString(),
        "Bearer mock-payment-key", StringComparison.Ordinal))
        return Results.Unauthorized();

    MockPaymentCheckoutRequest? payload;
    try
    {
        payload = await request.ReadFromJsonAsync<MockPaymentCheckoutRequest>(ct);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "invalid_json" });
    }
    if (payload is null)
        return Results.BadRequest(new { error = "invalid_json" });

    var result = MockPaymentCheckout.Create(payload.MerchantReference,
        payload.Amount, payload.Currency, payload.Description);
    return result is null
        ? Results.BadRequest(new { error = "invalid_checkout_request" })
        : Results.Ok(new
        {
            provider_order_id = result.ProviderOrderId,
            checkout_url = result.CheckoutUrl,
        });
});

app.MapPost("/v1/payments/refunds", async (HttpRequest request,
    CancellationToken ct) =>
{
    if (!string.Equals(request.Headers.Authorization.ToString(),
        "Bearer mock-payment-key", StringComparison.Ordinal))
        return Results.Unauthorized();
    MockPaymentRefundRequest? payload;
    try
    {
        payload = await request.ReadFromJsonAsync<MockPaymentRefundRequest>(ct);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "invalid_json" });
    }
    if (payload is null)
        return Results.BadRequest(new { error = "invalid_json" });
    var result = MockPaymentRefund.Create(payload.MerchantReference,
        payload.Amount, payload.Currency, request.Headers["Idempotency-Key"].ToString());
    return result is null
        ? Results.BadRequest(new { error = "invalid_refund_request" })
        : Results.Ok(new
        {
            provider_refund_id = result.ProviderRefundId,
            status = result.Status,
            amount = result.Amount,
            currency = result.Currency,
        });
});

app.MapGet("/v1/pricing", () => Results.Ok(new
{
    provider = "scalaapi-mock",
    data = new[]
    {
        new
        {
            model = "gpt-4o",
            input_usd_per_million = 2.50m,
            output_usd_per_million = 10m,
            cache_read_usd_per_million = 1.25m,
            cache_write_usd_per_million = 0m,
        },
        new
        {
            model = "claude-sonnet-4",
            input_usd_per_million = 3m,
            output_usd_per_million = 15m,
            cache_read_usd_per_million = 0.30m,
            cache_write_usd_per_million = 3.75m,
        },
        new
        {
            model = "gemini-2.5-flash",
            input_usd_per_million = 0.15m,
            output_usd_per_million = 0.60m,
            cache_read_usd_per_million = 0m,
            cache_write_usd_per_million = 0m,
        },
    },
}));

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

app.MapPost("/v1/classifier/evaluate", async (HttpContext context,
    CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    if (root.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty("content", out var content)
        || content.ValueKind != JsonValueKind.String
        || !root.TryGetProperty("pattern", out var pattern)
        || pattern.ValueKind != JsonValueKind.String
        || !root.TryGetProperty("evaluator_version", out var evaluator)
        || evaluator.ValueKind != JsonValueKind.String
        || evaluator.GetString() != "unicode-confusable-v1")
        return Results.BadRequest(new { error = "classifier_request_invalid" });

    var normalizedContent = content.GetString() ?? "";
    var normalizedPattern = pattern.GetString() ?? "";
    if (normalizedContent.Length > 128 * 1024 || normalizedPattern.Length is < 1 or > 1024)
        return Results.BadRequest(new { error = "classifier_request_too_large" });

    // These markers are source-owned fault fixtures used by contract and smoke
    // tests; no upstream provider protocol is being emulated here.
    if (normalizedPattern.Contains("external-classifier-outage-marker",
            StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (normalizedPattern.Contains("external-classifier-malformed-marker",
            StringComparison.Ordinal))
        return Results.Text("{not-json", "application/json", statusCode: 200);
    if (normalizedPattern.Contains("external-classifier-oversized-marker",
            StringComparison.Ordinal))
        return Results.Text(new string('x', 9 * 1024), "application/json", statusCode: 200);
    if (normalizedPattern.Contains("external-classifier-timeout-marker",
            StringComparison.Ordinal))
    {
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        return Results.Ok(new { outcome = "no_match" });
    }

    var matched = normalizedContent.Contains(normalizedPattern, StringComparison.Ordinal);
    return Results.Ok(new { outcome = matched ? "match" : "no_match" });
});

// Official OpenAI Moderations-shaped fixture for the production adapter. The
// deterministic markers keep this source-owned contract free of live API calls.
app.MapPost("/v1/moderations", async (HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(context.Request.Headers.Authorization.ToString(),
        "Bearer mock-openai-moderation-key", StringComparison.Ordinal))
        return Results.Unauthorized();

    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    if (root.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty("input", out var input)
        || input.ValueKind != JsonValueKind.String
        || !root.TryGetProperty("model", out var model)
        || model.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { error = "moderation_request_invalid" });

    var content = input.GetString() ?? "";
    var modelName = model.GetString() ?? "";
    if (content.Length > 128 * 1024 || modelName.Length is < 1 or > 128)
        return Results.BadRequest(new { error = "moderation_request_too_large" });
    if (content.Contains("openai-moderation-unavailable-marker",
            StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (content.Contains("openai-moderation-malformed-marker",
            StringComparison.Ordinal))
        return Results.Text("{not-json", "application/json", statusCode: 200);
    if (content.Contains("openai-moderation-oversized-marker",
            StringComparison.Ordinal))
        return Results.Text(new string('x', 17 * 1024), "application/json", statusCode: 200);
    var scenario = context.Request.Headers["X-Provider-Scenario"].FirstOrDefault() ?? "";
    if (scenario.Equals("openai-moderation-unavailable", StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (scenario.Equals("openai-moderation-malformed", StringComparison.Ordinal))
        return Results.Text("{not-json", "application/json", statusCode: 200);
    if (scenario.Equals("openai-moderation-oversized", StringComparison.Ordinal))
        return Results.Text(new string('x', 17 * 1024), "application/json", statusCode: 200);
    if (scenario.Equals("openai-moderation-timeout", StringComparison.Ordinal))
    {
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        return Results.Ok(new { id = "modr_timeout", model = modelName,
            results = new[] { new { flagged = false } } });
    }

    var flagged = content.Contains("openai-moderation-flag-marker",
        StringComparison.Ordinal);
    return Results.Ok(new
    {
        id = "modr_scalaapi_fixture",
        model = modelName,
        results = new[] { new { flagged } },
    });
});

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

app.MapPost("/v1/responses/compact", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    var scenario = MockProviderHelpers.Scenario(context, root).ToLowerInvariant();
    if (root.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty("model", out var modelValue)
        || modelValue.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(modelValue.GetString())
        || !root.TryGetProperty("input", out var input)
        || input.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = new { code = "invalid_compact_request" } });

    var model = modelValue.GetString()!;
    var inputTokens = MockProviderHelpers.EstimateInputTokens(root);
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? MockProviderHelpers.Id("resp_compact");
    var stream = root.TryGetProperty("stream", out var streamValue)
        && streamValue.ValueKind == JsonValueKind.True;
    if (root.TryGetProperty("stream", out streamValue)
        && streamValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        return Results.BadRequest(new { error = new { code = "invalid_compact_stream" } });
    if (scenario == "429")
        return Results.Json(new { error = new { code = "mock_rate_limited" } }, statusCode: 429);
    if (scenario == "500")
        return Results.Json(new { error = new { code = "mock_upstream_failure" } }, statusCode: 500);
    if (!stream && scenario == "malformed")
        return Results.Text("{not-json", "application/json", statusCode: 200);

    var compactItem = new
    {
        id = $"{requestId}_item_0",
        type = "compaction",
        status = "completed",
        encrypted_content = $"mock-compaction:{ResponseInputText(root)}",
    };
    var compactResponse = new
    {
        id = requestId,
        @object = "response",
        status = "completed",
        model,
        output = new[] { compactItem },
        usage = new { input_tokens = inputTokens, output_tokens = 4, total_tokens = inputTokens + 4 },
    };
    if (stream)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var events = new (string Type, object Payload)[]
        {
            ("response.created", new { type = "response.created", response = new { id = requestId, status = "in_progress", model } }),
            ("response.output_item.done", new { type = "response.output_item.done", item = compactItem }),
            ("response.completed", new { type = "response.completed", response = compactResponse }),
        };
        foreach (var item in events)
            await context.Response.WriteAsync(
                $"event: {item.Type}\ndata: {JsonSerializer.Serialize(item.Payload)}\n\n",
                cancellationToken);
        return Results.Empty;
    }
    return Results.Json(compactResponse);
});

app.MapPost("/v1/responses", async (HttpContext context, CancellationToken cancellationToken) =>
{
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    var model = MockProviderHelpers.Model(root);
    var inputTokens = MockProviderHelpers.EstimateInputTokens(root);
    var scenario = MockProviderHelpers.Scenario(context, root).ToLowerInvariant();
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? MockProviderHelpers.Id("resp");
    var stream = root.TryGetProperty("stream", out var streamValue)
        && streamValue.ValueKind == JsonValueKind.True;
    if (!stream && scenario == "malformed")
        return Results.Text("{not-json", "application/json", statusCode: 200);
    var usage = new { input_tokens = inputTokens, output_tokens = 5,
        total_tokens = inputTokens + 5 };
    var completedResponse = new
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
        usage
    };
    responses[requestId] = JsonSerializer.Serialize(completedResponse);
    var itemId = $"{requestId}_input_0";
    responseInputItems[requestId] = JsonSerializer.Serialize(new
    {
        @object = "list",
        data = new[]
        {
            new
            {
                id = itemId,
                @object = "response.input_item",
                type = "message",
                role = "user",
                content = new[]
                {
                    new { type = "input_text", text = ResponseInputText(root) }
                },
            }
        },
        first_id = itemId,
        last_id = itemId,
        has_more = false,
    });
    if (stream)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var events = new (string Type, object Payload)[]
        {
            ("response.created", new { type = "response.created", response = new { id = requestId, status = "in_progress", model } }),
            ("response.output_item.added", new { type = "response.output_item.added", item = new { type = "message", role = "assistant" } }),
            ("response.output_text.delta", new { type = "response.output_text.delta", delta = "mock response" }),
            ("response.output_text.done", new { type = "response.output_text.done", text = "mock response" }),
            ("response.output_item.done", new { type = "response.output_item.done", item = new { type = "message", role = "assistant" } }),
            ("response.completed", new { type = "response.completed", response = completedResponse }),
        };
        foreach (var item in events)
        {
            await context.Response.WriteAsync(
                $"event: {item.Type}\ndata: {JsonSerializer.Serialize(item.Payload)}\n\n",
                cancellationToken);
        }
        return Results.Empty;
    }
    return Results.Text(responses[requestId], "application/json");
});

app.MapGet("/v1/responses/{responseId}", (string responseId) =>
    responses.TryGetValue(responseId, out var response)
        ? Results.Text(response, "application/json")
        : Results.NotFound(new { error = new { code = "response_not_found" } }));

app.MapGet("/v1/responses/{responseId}/input_items", (string responseId) =>
    responses.ContainsKey(responseId)
        && responseInputItems.TryGetValue(responseId, out var inputItems)
        ? Results.Text(inputItems, "application/json")
        : Results.NotFound(new { error = new { code = "response_not_found" } }));

app.MapPost("/v1/responses/{responseId}/cancel", (string responseId) =>
{
    if (!responses.TryGetValue(responseId, out var current))
        return Results.NotFound(new { error = new { code = "response_not_found" } });

    var canceled = JsonNode.Parse(current)?.AsObject();
    if (canceled is null)
        return Results.Problem("Stored response cannot be canceled", statusCode: 500);

    canceled["status"] = "cancelled";
    var payload = canceled.ToJsonString();
    responses[responseId] = payload;
    return Results.Text(payload, "application/json");
});

app.MapDelete("/v1/responses/{responseId}", (string responseId) =>
{
    if (!responses.TryRemove(responseId, out _))
        return Results.NotFound(new { error = new { code = "response_not_found" } });

    responseInputItems.TryRemove(responseId, out _);
    return Results.Ok(new { id = responseId, @object = "response.deleted", deleted = true });
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
    var responseText = root.GetRawText() switch
    {
        var request when request.Contains("openai-moderation-flag-marker",
            StringComparison.Ordinal) => "openai-moderation-flag-marker",
        var request when request.Contains("openai-moderation-unavailable-marker",
            StringComparison.Ordinal) => "openai-moderation-unavailable-marker",
        _ => "mock response",
    };

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
                // A zero-length success body completes deterministically without
                // a terminal SSE event; the Gateway must retain the hold. A
                // hard Kestrel abort after a zero Content-Length can leave the
                // Photon client waiting for its socket timeout instead of
                // delivering EOF, so abrupt resets are covered by disconnect.
                context.Response.ContentLength = 0;
                await context.Response.StartAsync(cancellationToken);
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
        await context.Response.WriteAsync($"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":\"{responseText}\"}},\"finish_reason\":null}}]}}\n\n", cancellationToken);
        await context.Response.WriteAsync($"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{JsonSerializer.Serialize(usage)}}}\n\n", cancellationToken);
        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        return;
    }

    await context.Response.WriteAsJsonAsync(new
    {
        id = requestId,
        @object = "chat.completion",
        model,
        choices = new[] { new { index = 0, message = new { role = "assistant", content = responseText }, finish_reason = "stop" } },
        usage
    }, cancellationToken);
});

app.MapGet("/v1/requests/{requestId}", (string requestId) => Results.Ok(new
{
    request_id = requestId,
    usage = new { prompt_tokens = 7, completion_tokens = 5, total_tokens = 12 }
}));

static string ResponseInputText(JsonElement root)
{
    if (!root.TryGetProperty("input", out var input))
        return string.Empty;
    if (input.ValueKind == JsonValueKind.String)
        return input.GetString() ?? string.Empty;
    if (input.ValueKind != JsonValueKind.Array)
        return string.Empty;

    foreach (var item in input.EnumerateArray())
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("content", out var content))
            continue;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            continue;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? string.Empty;
        }
    }
    return string.Empty;
}

app.Run();

public partial class Program;

public sealed record MockPaymentCheckoutRequest(
    [property: JsonPropertyName("merchant_reference")] string MerchantReference,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("description")] string? Description);

public sealed record MockPaymentRefundRequest(
    [property: JsonPropertyName("merchant_reference")] string MerchantReference,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("provider_order_id")] string? ProviderOrderId,
    [property: JsonPropertyName("provider_payment_id")] string? ProviderPaymentId,
    [property: JsonPropertyName("reason")] string? Reason);
