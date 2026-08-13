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
var mediaStatuses = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
var cancellationObservations = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
var mediaPollDelayMs = Math.Clamp(
    builder.Configuration.GetValue<int>("MediaPollDelayMs"), 0, 30000);
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", provider = "scalaapi-mock" }));
app.MapGet("/__test/cancellations/{requestId}", (string requestId) =>
{
    cancellationObservations.TryGetValue($"anthropic:{requestId}", out var anthropic);
    cancellationObservations.TryGetValue($"gemini:{requestId}", out var gemini);
    return Results.Ok(new
    {
        request_id = requestId,
        anthropic,
        gemini,
        total = anthropic + gemini,
    });
});

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
            new { id = "jina-embeddings-v5-text-small", @object = "model", created = 1_700_000_002L, owned_by = "scalaapi-provider-mock" },
            new { id = "gemini-embedding-001", @object = "model", created = 1_700_000_003L, owned_by = "scalaapi-provider-mock" },
            new { id = "mock-image-1", @object = "model", created = 1_700_000_004L, owned_by = "scalaapi-provider-mock" },
            new { id = "mock-video-1", @object = "model", created = 1_700_000_005L, owned_by = "scalaapi-provider-mock" },
        }
    });
});

app.MapGet("/v1beta/models", (HttpRequest request) =>
    AuthenticateGemini(request) ?? Results.Ok(new
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

app.MapGet("/v1beta/models/{model}", (string model, HttpRequest request) =>
    AuthenticateGemini(request) ?? Results.Ok(new
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
    var model = MockProviderHelpers.Model(root, "");
    if (!MockProviderHelpers.TryGetEmbeddingProfile(model, out var profile))
        return Results.Json(new { error = new { code = "unsupported_embedding_model" } }, statusCode: 400);
    var scenario = MockProviderHelpers.Scenario(context, root).ToLowerInvariant();
    if (scenario == "429")
        return Results.Json(new { error = new { code = "mock_rate_limited" } }, statusCode: 429);
    if (scenario == "500")
        return Results.Json(new { error = new { code = "mock_upstream_failure" } }, statusCode: 500);
    if (scenario == "malformed")
        return Results.Text("{not-json", "application/json", statusCode: 200);

    var inputCount = MockProviderHelpers.EmbeddingInputCount(root);
    var dimensions = MockProviderHelpers.EmbeddingDimensions(root, profile.DefaultDimensions);
    var encoding = MockProviderHelpers.EmbeddingEncoding(root);
    if (inputCount is < 1 or > 2048 || dimensions < 1 || dimensions > profile.MaxDimensions
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
    var inputTokens = MockProviderHelpers.EstimateEmbeddingInputTokens(root, model);
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
    if (AuthenticateAnthropic(context.Request) is { } authError)
        return authError;
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
    if (AuthenticateAnthropic(context.Request) is { } authError)
        return authError;
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var root = body.RootElement;
    var model = MockProviderHelpers.Model(root, "claude-3-5-sonnet");
    var inputTokens = MockProviderHelpers.EstimateInputTokens(root);
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? context.Request.Headers["X-Request-ID"].FirstOrDefault()
        ?? MockProviderHelpers.Id("msg");
    var stream = root.TryGetProperty("stream", out var streamValue)
        && streamValue.ValueKind == JsonValueKind.True;
    var scenario = MockProviderHelpers.Scenario(context, root).ToLowerInvariant();
    if (scenario == "success"
        && root.TryGetProperty("metadata", out var metadata)
        && metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty("user_id", out var userId)
        && userId.GetString() == "scalaapi-json-stream")
        scenario = "json_stream";

    if (scenario == "429")
        return Results.Json(new { error = new { type = "rate_limit_error", message = "mock rate limited" } },
            statusCode: StatusCodes.Status429TooManyRequests);
    if (scenario == "500")
        return Results.Json(new { error = new { type = "api_error", message = "mock upstream failure" } },
            statusCode: StatusCodes.Status500InternalServerError);
    if (scenario == "timeout")
    {
        await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
        return Results.Empty;
    }
    if (scenario == "malformed")
        return stream
            ? Results.Text("event: message_start\ndata: {not-json\n\n", "text/event-stream")
            : Results.Text("{not-json", "application/json", statusCode: StatusCodes.Status200OK);
    if (scenario == "invalid_content_type")
        return Results.Text("event: message_start\ndata: {not-json\n\n", "application/json",
            statusCode: StatusCodes.Status200OK);
    if (scenario is "disconnect" or "disconnect_before_output")
    {
        if (stream && scenario == "disconnect")
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                $"event: content_block_delta\ndata: {JsonSerializer.Serialize(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "partial" } })}\n\n",
                cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        else if (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength = 0;
            await context.Response.StartAsync(cancellationToken);
        }
        context.Abort();
        return Results.Empty;
    }
    if (scenario == "disconnect_after_usage" && stream)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(
            $"event: message_start\ndata: {JsonSerializer.Serialize(new { type = "message_start", message = new { id = requestId, type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = inputTokens, output_tokens = 0 } } })}\n\n",
            cancellationToken);
        await context.Response.WriteAsync(
            $"event: message_delta\ndata: {JsonSerializer.Serialize(new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 5 } })}\n\n",
            cancellationToken);
        return Results.Empty;
    }
    if (scenario == "client_disconnect" && stream)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        try
        {
            await context.Response.StartAsync(cancellationToken);
            await context.Response.WriteAsync(
                $"event: content_block_delta\ndata: {JsonSerializer.Serialize(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "first" } })}\n\n",
                cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                await context.Response.WriteAsync(
                    $"event: content_block_delta\ndata: {JsonSerializer.Serialize(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "continued" } })}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            cancellationObservations.AddOrUpdate(
                $"anthropic:{requestId}", 1, static (_, count) => count + 1);
        }
        return Results.Empty;
    }
    if (scenario == "tool_call")
    {
        if (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var toolEvents = new (string Name, object Payload)[]
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
                    content_block = new { type = "tool_use", id = "toolu_mock_1", name = "get_weather", input = new { } }
                }),
                ("content_block_delta", new
                {
                    type = "content_block_delta",
                    index = 0,
                    delta = new { type = "input_json_delta", partial_json = "{\"city\":\"Vienna\"}" }
                }),
                ("content_block_stop", new { type = "content_block_stop", index = 0 }),
                ("message_delta", new
                {
                    type = "message_delta",
                    delta = new { stop_reason = "tool_use", stop_sequence = (string?)null },
                    usage = new { output_tokens = 8 }
                }),
                ("message_stop", new { type = "message_stop" }),
            };
            foreach (var item in toolEvents)
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
            content = new object[]
            {
                new { type = "text", text = "Let me check the weather." },
                new { type = "tool_use", id = "toolu_mock_1", name = "get_weather", input = new { city = "Vienna" } },
            },
            stop_reason = "tool_use",
            stop_sequence = (string?)null,
            usage = new { input_tokens = inputTokens, output_tokens = 8 }
        });
    }
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
    if (scenario == "tool_call")
    {
        var toolUsage = new { input_tokens = inputTokens, output_tokens = 8,
            total_tokens = inputTokens + 8 };
        var toolCallItem = new
        {
            type = "function_call",
            id = "fc_mock_1",
            call_id = "call_mock_1",
            name = "get_weather",
            arguments = "{\"city\":\"Vienna\"}",
        };
        var toolCompletedResponse = new
        {
            id = requestId,
            @object = "response",
            status = "completed",
            model,
            output = new object[] { toolCallItem },
            usage = toolUsage,
        };
        if (stream)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var toolEvents = new (string Type, object Payload)[]
            {
                ("response.created", new { type = "response.created", response = new { id = requestId, status = "in_progress", model } }),
                ("response.output_item.added", new { type = "response.output_item.added", output_index = 0, item = toolCallItem }),
                ("response.function_call_arguments.delta", new { type = "response.function_call_arguments.delta", item_id = "fc_mock_1", output_index = 0, delta = "{\"city\":\"Vienna\"}" }),
                ("response.function_call_arguments.done", new { type = "response.function_call_arguments.done", item_id = "fc_mock_1", output_index = 0, arguments = "{\"city\":\"Vienna\"}" }),
                ("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item = toolCallItem }),
                ("response.completed", new { type = "response.completed", response = toolCompletedResponse }),
            };
            foreach (var item in toolEvents)
            {
                await context.Response.WriteAsync(
                    $"event: {item.Type}\ndata: {JsonSerializer.Serialize(item.Payload)}\n\n",
                    cancellationToken);
            }
            return Results.Empty;
        }
        return Results.Json(toolCompletedResponse);
    }
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
    if (AuthenticateGemini(context.Request) is { } authError)
        return authError;
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var scenario = MockProviderHelpers.Scenario(context, body.RootElement).ToLowerInvariant();
    if (scenario == "429")
        return Results.Json(new { error = new { status = "RESOURCE_EXHAUSTED", message = "mock rate limited" } },
            statusCode: StatusCodes.Status429TooManyRequests);
    if (scenario == "500")
        return Results.Json(new { error = new { status = "INTERNAL", message = "mock upstream failure" } },
            statusCode: StatusCodes.Status500InternalServerError);
    if (scenario == "timeout")
    {
        await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
        return Results.Empty;
    }
    if (scenario == "malformed")
        return Results.Text("{not-json", "application/json", statusCode: StatusCodes.Status200OK);
    if (scenario == "disconnect")
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync("{\"candidates\":[", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        context.Abort();
        return Results.Empty;
    }
    var inputTokens = MockProviderHelpers.EstimateInputTokens(body.RootElement);
    if (scenario == "tool_call")
    {
        return Results.Ok(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        role = "model",
                        parts = new[]
                        {
                            new
                            {
                                functionCall = new
                                {
                                    name = "get_weather",
                                    args = new { city = "Vienna" }
                                }
                            }
                        }
                    },
                    finishReason = "STOP",
                    index = 0
                }
            },
            usageMetadata = new
            {
                promptTokenCount = inputTokens,
                candidatesTokenCount = 8,
                totalTokenCount = inputTokens + 8
            },
            modelVersion = model
        });
    }
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
    if (AuthenticateGemini(context.Request) is { } authError)
        return authError;
    using var body = await MockProviderHelpers.ReadJsonAsync(context, cancellationToken);
    var scenario = MockProviderHelpers.Scenario(context, body.RootElement).ToLowerInvariant();
    var requestId = context.Request.Headers["X-Provider-Request-Id"].FirstOrDefault()
        ?? context.Request.Headers["X-Request-ID"].FirstOrDefault()
        ?? MockProviderHelpers.Id("gemini");
    if (scenario == "429")
        return Results.Json(new { error = new { status = "RESOURCE_EXHAUSTED", message = "mock rate limited" } },
            statusCode: StatusCodes.Status429TooManyRequests);
    if (scenario == "500")
        return Results.Json(new { error = new { status = "INTERNAL", message = "mock upstream failure" } },
            statusCode: StatusCodes.Status500InternalServerError);
    if (scenario == "timeout")
    {
        await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
        return Results.Empty;
    }
    if (scenario == "malformed")
        return Results.Text("data: {not-json\n\n", "text/event-stream", statusCode: StatusCodes.Status200OK);
    if (scenario == "invalid_content_type")
        return Results.Text("data: {not-json\n\n", "application/json", statusCode: StatusCodes.Status200OK);
    if (scenario is "disconnect" or "disconnect_before_output")
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        if (scenario == "disconnect")
        {
            await context.Response.WriteAsync("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        else
        {
            context.Response.ContentLength = 0;
            await context.Response.StartAsync(cancellationToken);
        }
        context.Abort();
        return Results.Empty;
    }
    if (scenario == "disconnect_after_usage")
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { candidates = Array.Empty<object>(), usageMetadata = new { promptTokenCount = MockProviderHelpers.EstimateInputTokens(body.RootElement), candidatesTokenCount = 5, totalTokenCount = MockProviderHelpers.EstimateInputTokens(body.RootElement) + 5 } })}\n\n", cancellationToken);
        return Results.Empty;
    }
    if (scenario == "client_disconnect")
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        try
        {
            await context.Response.StartAsync(cancellationToken);
            await context.Response.WriteAsync(
                "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"first\"}]},\"finishReason\":null}]}\n\n",
                cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                await context.Response.WriteAsync(
                    "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"continued\"}]},\"finishReason\":null}]}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            cancellationObservations.AddOrUpdate(
                $"gemini:{requestId}", 1, static (_, count) => count + 1);
        }
        return Results.Empty;
    }
    var inputTokens = MockProviderHelpers.EstimateInputTokens(body.RootElement);
    if (scenario == "tool_call")
    {
        context.Response.ContentType = "text/event-stream";
        var toolCallPayload = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        role = "model",
                        parts = new[]
                        {
                            new
                            {
                                functionCall = new
                                {
                                    name = "get_weather",
                                    args = new { city = "Vienna" }
                                }
                            }
                        }
                    },
                    finishReason = "STOP"
                }
            },
            usageMetadata = new
            {
                promptTokenCount = inputTokens,
                candidatesTokenCount = 8,
                totalTokenCount = inputTokens + 8
            },
            modelVersion = model
        });
        await context.Response.WriteAsync($"data: {toolCallPayload}\n\n", cancellationToken);
        return Results.Empty;
    }
    context.Response.ContentType = "text/event-stream";
    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = "mock response" } } }, finishReason = "STOP" } },
        usageMetadata = new { promptTokenCount = inputTokens, candidatesTokenCount = 5, totalTokenCount = inputTokens + 5 },
        modelVersion = model
    })}\n\n", cancellationToken);
    return Results.Empty;
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

app.MapPost("/v1/images/generations/async", () =>
{
    var id = MockProviderHelpers.Id("image-task");
    mediaStatuses[id] = "pending";
    return Results.Accepted(value: new
    {
        id,
        status = "pending",
        progress = 0
    });
});

app.MapPost("/v1/images/edits/async", () =>
{
    var id = MockProviderHelpers.Id("image-edit-task");
    mediaStatuses[id] = "pending";
    return Results.Accepted(value: new
    {
        id,
        status = "pending",
        progress = 0
    });
});

app.MapPost("/v1/images/batches", () =>
{
    var id = MockProviderHelpers.Id("image-batch");
    mediaStatuses[id] = "pending";
    return Results.Accepted(value: new
    {
        id,
        status = "pending",
        progress = 0
    });
});

app.MapPost("/v1/images/tasks/{taskId}/cancel", (string taskId) =>
{
    if (!mediaStatuses.ContainsKey(taskId))
        return Results.NotFound(new { error = "media_task_not_found" });
    mediaStatuses[taskId] = "canceled";
    return Results.Ok(new { id = taskId, status = "canceled", progress = 100 });
});

app.MapPost("/v1/images/batches/{batchId}/cancel", (string batchId) =>
{
    if (!mediaStatuses.ContainsKey(batchId))
        return Results.NotFound(new { error = "media_batch_not_found" });
    mediaStatuses[batchId] = "canceled";
    return Results.Ok(new { id = batchId, status = "canceled", progress = 100 });
});

app.MapGet("/v1/images/tasks/{taskId}", async (string taskId, CancellationToken ct) =>
{
    if (mediaPollDelayMs > 0)
        await Task.Delay(mediaPollDelayMs, ct);
    var status = mediaStatuses.TryGetValue(taskId, out var current)
        && current == "canceled" ? "canceled" : "succeeded";
    object[] data = status == "canceled"
        ? []
        : [new { url = MockProviderHelpers.OutputUrl(taskId) }];
    return Results.Ok(new
    {
        id = taskId,
        status,
        progress = 100,
        output_url = status == "canceled" ? "" : MockProviderHelpers.OutputUrl(taskId),
        content_type = "image/png",
        data,
        size = "1024x1024"
    });
});

app.MapGet("/v1/images/batches/{batchId}", async (string batchId, CancellationToken ct) =>
{
    if (mediaPollDelayMs > 0)
        await Task.Delay(mediaPollDelayMs, ct);
    var status = mediaStatuses.TryGetValue(batchId, out var current)
        && current == "canceled" ? "canceled" : "succeeded";
    object[] data = status == "canceled"
        ? []
        : [new { custom_id = "mock-1", url = MockProviderHelpers.OutputUrl(batchId) }];
    return Results.Ok(new
    {
        id = batchId,
        status,
        progress = 100,
        output_url = status == "canceled" ? "" : MockProviderHelpers.OutputUrl(batchId),
        content_type = "application/json",
        data
    });
});

app.MapGet("/v1/images/batches/models", () => Results.Ok(new
{
    @object = "list",
    data = new[] { new { id = "mock-image-1", @object = "model", owned_by = "scalaapi-provider-mock" } }
}));

app.MapPost("/v1/videos/generations", () =>
{
    var id = MockProviderHelpers.Id("video");
    mediaStatuses[id] = "pending";
    return Results.Accepted(value: new
    {
        id,
        status = "pending",
        progress = 0
    });
});

app.MapPost("/v1/videos/edits", () =>
{
    var id = MockProviderHelpers.Id("video-edit");
    mediaStatuses[id] = "pending";
    return Results.Accepted(value: new
    {
        id,
        status = "pending",
        progress = 0
    });
});

app.MapPost("/v1/videos/extensions", () =>
{
    var id = MockProviderHelpers.Id("video-extension");
    mediaStatuses[id] = "pending";
    return Results.Accepted(value: new
    {
        id,
        status = "pending",
        progress = 0
    });
});

app.MapPost("/v1/videos/{videoId}/cancel", (string videoId) =>
{
    if (!mediaStatuses.ContainsKey(videoId))
        return Results.NotFound(new { error = "video_not_found" });
    mediaStatuses[videoId] = "canceled";
    return Results.Ok(new { id = videoId, status = "canceled", progress = 100 });
});

app.MapGet("/v1/videos/{videoId}", (string videoId) =>
{
    var status = mediaStatuses.TryGetValue(videoId, out var current)
        && current == "canceled" ? "canceled" : "succeeded";
    return Results.Ok(new
    {
        id = videoId,
        status,
        progress = 100,
        output_url = status == "canceled" ? "" : MockProviderHelpers.OutputUrl(videoId),
        content_type = "video/mp4",
        resolution = "1280x720",
        duration = 4
    });
});

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

    var usage = new { prompt_tokens = 7, completion_tokens = 5, total_tokens = 12 };
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
        case "tool_call":
            if (stream)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{{\"id\":\"call_mock_1\",\"type\":\"function\",\"function\":{{\"name\":\"get_weather\",\"arguments\":\"\"}}}}]}},\"finish_reason\":null}}]}}\n\n",
                    cancellationToken);
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":0,\"function\":{{\"arguments\":\"{{\\\"city\\\":\\\"Vienna\\\"}}\"}}}}]}},\"finish_reason\":null}}]}}\n\n",
                    cancellationToken);
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"tool_calls\"}}],\"usage\":{JsonSerializer.Serialize(usage)}}}\n\n",
                    cancellationToken);
                await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                return;
            }
            await context.Response.WriteAsJsonAsync(new
            {
                id = requestId,
                @object = "chat.completion",
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_mock_1", type = "function", function = new { name = "get_weather", arguments = "{\"city\":\"Vienna\"}" } } } }, finish_reason = "tool_calls" } },
                usage
            }, cancellationToken);
            return;
        case "multi_choice":
            if (stream)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":\"choice A\"}},\"finish_reason\":null}},{{\"index\":1,\"delta\":{{\"role\":\"assistant\",\"content\":\"choice B\"}},\"finish_reason\":null}}]}}\n\n",
                    cancellationToken);
                await context.Response.WriteAsync(
                    $"data: {{\"id\":\"{requestId}\",\"object\":\"chat.completion.chunk\",\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}},{{\"index\":1,\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{JsonSerializer.Serialize(usage)}}}\n\n",
                    cancellationToken);
                await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                return;
            }
            await context.Response.WriteAsJsonAsync(new
            {
                id = requestId,
                @object = "chat.completion",
                model,
                choices = new object[]
                {
                    new { index = 0, message = new { role = "assistant", content = "choice A" }, finish_reason = "stop" },
                    new { index = 1, message = new { role = "assistant", content = "choice B" }, finish_reason = "stop" },
                },
                usage
            }, cancellationToken);
            return;
    }

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

static IResult? AuthenticateAnthropic(HttpRequest request)
{
    var nativeKey = string.Equals(request.Headers["x-api-key"],
        "scalaapi-mock-key", StringComparison.Ordinal);
    var oauth = MockOAuthTokenEndpoint.IsAcceptedAccessHeader(
        request.Headers.Authorization.ToString());
    if (request.Headers.ContainsKey("api_key") || (!nativeKey && !oauth))
    {
        return Results.Json(new
        {
            type = "error",
            error = new { type = "authentication_error", message = "invalid provider credentials" }
        }, statusCode: StatusCodes.Status401Unauthorized);
    }
    if (!string.Equals(request.Headers["anthropic-version"], "2023-06-01",
            StringComparison.Ordinal)
        || request.Headers["anthropic-beta"].ToString().Length > 256)
    {
        return Results.Json(new
        {
            type = "error",
            error = new { type = "invalid_request_error", message = "invalid provider version" }
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return null;
}

static IResult? AuthenticateGemini(HttpRequest request)
{
    var nativeKey = string.Equals(request.Headers["x-goog-api-key"],
        "scalaapi-mock-key", StringComparison.Ordinal);
    var oauth = MockOAuthTokenEndpoint.IsAcceptedAccessHeader(
        request.Headers.Authorization.ToString());
    if (!request.Headers.ContainsKey("api_key") && (nativeKey || oauth))
        return null;
    return Results.Json(new
    {
        error = new
        {
            code = StatusCodes.Status401Unauthorized,
            status = "UNAUTHENTICATED",
            message = "invalid provider credentials"
        }
    }, statusCode: StatusCodes.Status401Unauthorized);
}

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
