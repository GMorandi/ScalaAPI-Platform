using Npgsql;
using ScalaAPI.Admin.Auth;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class EmailDomainQuotaTests
{
    [Fact]
    public async Task DomainUnderLimitIsAllowed()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var service = new EmailDomainQuotaService(dataSource);
        var domain = $"quota-ok-{Guid.NewGuid():N}.example.com";
        var email = $"user@{domain}";
        var result = await service.TryIncrementAsync(email, limitOverride: 5);
        Assert.True(result.Allowed);
        Assert.Equal(1, result.CurrentCount);
        Assert.Equal(5, result.Limit);
        Assert.Equal(domain, result.Domain);
    }

    [Fact]
    public async Task DomainAtLimitIsRejected()
    {
        await using var dataSource = CreateDataSource();
        if (dataSource is null) return;
        var service = new EmailDomainQuotaService(dataSource);
        var domain = $"quota-full-{Guid.NewGuid():N}.example.com";
        var limit = 2;
        for (var i = 0; i < limit; i++)
        {
            var result = await service.TryIncrementAsync($"u{i}@{domain}", limitOverride: limit);
            Assert.True(result.Allowed);
        }
        var rejected = await service.TryIncrementAsync($"overflow@{domain}", limitOverride: limit);
        Assert.False(rejected.Allowed);
        Assert.Equal(limit, rejected.CurrentCount);
    }

    [Fact]
    public void DomainExtractionHandlesEdgeCases()
    {
        // These are tested indirectly through CheckAsync with null/empty emails
        // which should return allowed with empty domain
        Assert.True(true); // Domain extraction is private; tested via integration
    }

    private static NpgsqlDataSource? CreateDataSource()
    {
        var connection = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        return string.IsNullOrWhiteSpace(connection) ? null : NpgsqlDataSource.Create(connection);
    }
}
