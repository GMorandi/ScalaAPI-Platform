extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockAudioHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockAudioHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory) => this.factory = factory;

    // --- TTS Tests ---

    [Fact]
    public async Task TtsSuccessReturnsAudioContent()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/audio/speech", new
        {
            model = "tts-1",
            input = "Hello world",
            voice = "alloy",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("audio", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
        Assert.True(response.Headers.TryGetValues("X-Mock-Model", out var models));
        Assert.Contains("tts-1", models);
        Assert.True(response.Headers.TryGetValues("X-Mock-Voice", out var voices));
        Assert.Contains("alloy", voices);
    }

    [Fact]
    public async Task TtsWavFormatReturnsWavContentType()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/audio/speech", new
        {
            model = "tts-1",
            input = "Hello",
            voice = "shimmer",
            response_format = "wav",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TtsRateLimitedReturns429()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/audio/speech")
        {
            Content = JsonContent.Create(new { model = "tts-1", input = "test", voice = "alloy" }),
        };
        request.Headers.Add("X-Mock-Scenario", "rate_limited");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Contains("5", values);
    }

    [Fact]
    public async Task TtsServerErrorReturns500()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/audio/speech")
        {
            Content = JsonContent.Create(new { model = "tts-1", input = "test", voice = "alloy" }),
        };
        request.Headers.Add("X-Mock-Scenario", "server_error");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task TtsMissingAuthReturns401()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/alpha/audio/speech", new
        {
            model = "tts-1", input = "test", voice = "alloy",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TtsMissingInputReturns400()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/audio/speech", new
        {
            model = "tts-1", voice = "alloy",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TtsMissingVoiceReturns400()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/audio/speech", new
        {
            model = "tts-1", input = "hello",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TtsInputExceedingLimitReturns400()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/audio/speech", new
        {
            model = "tts-1", input = new string('x', 4097), voice = "alloy",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- STT Tests ---

    [Fact]
    public async Task SttSuccessReturnsTranscript()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/audio/transcriptions", new
        {
            model = "whisper-1",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("id", out var id));
        Assert.StartsWith("transcription-", id.GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("text").GetString()));
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.True(root.GetProperty("duration_sec").GetDouble() > 0);
    }

    [Fact]
    public async Task SttEmptyScenarioReturnsEmptyText()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/audio/transcriptions")
        {
            Content = JsonContent.Create(new { model = "whisper-1" }),
        };
        request.Headers.Add("X-Mock-Scenario", "empty");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal(0.0, doc.RootElement.GetProperty("duration_sec").GetDouble());
    }

    [Fact]
    public async Task SttRateLimitedReturns429()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/audio/transcriptions")
        {
            Content = JsonContent.Create(new { model = "whisper-1" }),
        };
        request.Headers.Add("X-Mock-Scenario", "rate_limited");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task SttMissingAuthReturns401()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/alpha/audio/transcriptions", new
        {
            model = "whisper-1",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
