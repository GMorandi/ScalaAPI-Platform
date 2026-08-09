using Npgsql;
using ScalaAPI.Admin.Auth;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class AuthAbuseServiceTests
{
    [Fact]
    public void InputValidationNormalizesEmailAndBoundsCredentials()
    {
        Assert.True(AuthInputValidation.TryNormalizeEmail(" User@Example.COM ", out var email));
        Assert.Equal("user@example.com", email);
        Assert.False(AuthInputValidation.TryNormalizeEmail("not-an-email", out _));
        Assert.False(AuthInputValidation.TryNormalizeEmail("user@example.com extra", out _));
        Assert.True(AuthInputValidation.IsValidPassword(new string('a', 12)));
        Assert.False(AuthInputValidation.IsValidPassword(new string('a', 11)));
        Assert.False(AuthInputValidation.IsValidPassword(new string('a', 257)));
    }

    [Fact]
    public async Task LoginCounterLocksAfterFiveFailuresAndReturnsRetryAfter()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var service = new AuthAbuseService(dataSource);
        var email = $"abuse-{Guid.NewGuid():N}@example.com";
        var ip = $"198.51.100.{Random.Shared.Next(1, 254)}";

        for (var i = 0; i < AuthAbuseService.LoginFailureLimit; i++)
        {
            var before = await service.CheckLoginAsync(email, ip);
            Assert.True(before.Allowed);
            await service.RecordLoginFailureAsync(email, ip);
        }

        var locked = await service.CheckLoginAsync(email, ip);
        Assert.False(locked.Allowed);
        Assert.InRange(locked.RetryAfterSeconds, 1, 901);
    }

    [Fact]
    public async Task SuccessfulLoginClearsIdentityAndIpCounters()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var service = new AuthAbuseService(dataSource);
        var email = $"success-{Guid.NewGuid():N}@example.com";
        var ip = $"203.0.113.{Random.Shared.Next(1, 254)}";

        await service.RecordLoginFailureAsync(email, ip);
        await service.RecordLoginSuccessAsync(email, ip);

        Assert.True((await service.CheckLoginAsync(email, ip)).Allowed);
    }

    [Fact]
    public async Task LoginIpCounterIsIndependentFromAnotherIdentity()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var service = new AuthAbuseService(dataSource);
        var ip = $"192.0.2.{Random.Shared.Next(1, 254)}";

        for (var i = 0; i < AuthAbuseService.LoginFailureLimit; i++)
            await service.RecordLoginFailureAsync($"one-{Guid.NewGuid():N}@example.com", ip);

        var blocked = await service.CheckLoginAsync("another@example.com", ip);
        Assert.True(blocked.Allowed);
    }

    [Fact]
    public async Task RegistrationCounterHasSeparateHourlyBudget()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var service = new AuthAbuseService(dataSource);
        var ip = $"198.18.0.{Random.Shared.Next(1, 254)}";

        for (var i = 0; i < AuthAbuseService.RegistrationFailureLimit; i++)
            await service.RecordRegistrationFailureAsync(ip);

        var locked = await service.CheckRegistrationAsync(ip);
        Assert.False(locked.Allowed);
        Assert.InRange(locked.RetryAfterSeconds, 1, 3601);
    }

    private static NpgsqlDataSource? CreateDataSource()
    {
        var connection = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        return string.IsNullOrWhiteSpace(connection) ? null : NpgsqlDataSource.Create(connection);
    }
}
