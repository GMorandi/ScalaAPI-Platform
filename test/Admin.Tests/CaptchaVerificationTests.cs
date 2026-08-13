using Npgsql;
using ScalaAPI.Admin.Auth;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class CaptchaVerificationTests
{
    [Fact]
    public async Task PassTokenIsAccepted()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var provider = new MockCaptchaProvider();
        var service = new CaptchaVerificationService(dataSource, provider);
        var nonce = await service.IssueChallengeAsync("register");
        var result = await service.VerifyAsync(nonce, "mock-captcha-pass-1", "register", "127.0.0.1");
        Assert.True(result.Accepted);
        Assert.Equal(CaptchaDecision.Accepted, result.Decision);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public async Task FailTokenIsRejected()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var provider = new MockCaptchaProvider();
        var service = new CaptchaVerificationService(dataSource, provider);
        var nonce = await service.IssueChallengeAsync("register");
        var result = await service.VerifyAsync(nonce, "mock-captcha-fail-1", "register", "127.0.0.1");
        Assert.False(result.Accepted);
        Assert.Equal(CaptchaDecision.Rejected, result.Decision);
    }

    [Fact]
    public async Task TimeoutTokenResultsInProviderFailure()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var provider = new MockCaptchaProvider();
        var service = new CaptchaVerificationService(dataSource, provider);
        var nonce = await service.IssueChallengeAsync("register");
        var result = await service.VerifyAsync(nonce, "mock-captcha-timeout-1", "register", "127.0.0.1");
        Assert.False(result.Accepted);
        Assert.Equal(CaptchaDecision.ProviderFailure, result.Decision);
    }

    [Fact]
    public async Task ReplayIsDetected()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var provider = new MockCaptchaProvider();
        var service = new CaptchaVerificationService(dataSource, provider);
        var nonce = await service.IssueChallengeAsync("register");
        var first = await service.VerifyAsync(nonce, "mock-captcha-pass-1", "register", "127.0.0.1");
        Assert.True(first.Accepted);
        var replay = await service.VerifyAsync(nonce, "mock-captcha-pass-1", "register", "127.0.0.1");
        Assert.False(replay.Accepted);
        Assert.Equal(CaptchaDecision.ReplayDetected, replay.Decision);
    }

    [Fact]
    public async Task MissingInputIsRejected()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var provider = new MockCaptchaProvider();
        var service = new CaptchaVerificationService(dataSource, provider);
        var result = await service.VerifyAsync(null, null, "register", "127.0.0.1");
        Assert.False(result.Accepted);
        Assert.Equal(CaptchaDecision.Rejected, result.Decision);
        Assert.Equal("missing_input", result.ErrorCode);
    }

    private static NpgsqlDataSource? CreateDataSource()
    {
        var connection = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        return string.IsNullOrWhiteSpace(connection) ? null : NpgsqlDataSource.Create(connection);
    }
}
