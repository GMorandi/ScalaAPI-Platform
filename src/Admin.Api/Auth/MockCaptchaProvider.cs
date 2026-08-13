namespace ScalaAPI.Admin.Auth;

public sealed class MockCaptchaProvider : ICaptchaProvider
{
    public string Name => "mock";
    public Task<CaptchaProviderResponse> VerifyAsync(string token, string? remoteIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(new CaptchaProviderResponse(CaptchaProviderStatus.InvalidToken, 0.0, "missing_token"));
        if (token.StartsWith("mock-captcha-pass-", StringComparison.Ordinal))
            return Task.FromResult(new CaptchaProviderResponse(CaptchaProviderStatus.Success, 1.0));
        if (token.StartsWith("mock-captcha-fail-", StringComparison.Ordinal))
            return Task.FromResult(new CaptchaProviderResponse(CaptchaProviderStatus.InvalidToken, 0.0, "mock_failure"));
        if (token.StartsWith("mock-captcha-timeout-", StringComparison.Ordinal))
            return Task.FromResult(new CaptchaProviderResponse(CaptchaProviderStatus.Timeout, 0.0, "mock_timeout"));
        return Task.FromResult(new CaptchaProviderResponse(CaptchaProviderStatus.InvalidToken, 0.0, "unrecognised_mock_token"));
    }
}
