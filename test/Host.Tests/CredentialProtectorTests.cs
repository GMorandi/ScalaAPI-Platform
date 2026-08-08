using Microsoft.Extensions.Configuration;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public class CredentialProtectorTests
{
    [Fact]
    public void EncryptsWithRandomNonceAndRoundTrips()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Security:MasterKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }).Build();
        var protector = new CredentialProtector(config);

        var first = protector.Protect("Bearer upstream-secret");
        var second = protector.Protect("Bearer upstream-secret");

        Assert.StartsWith("enc:v1:", first);
        Assert.NotEqual(first, second);
        Assert.Equal("Bearer upstream-secret", protector.Unprotect(first));
    }

    [Fact]
    public void RejectsInvalidKeyLength()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Security:MasterKey"] = "c2hvcnQ=" }).Build();

        Assert.Throws<InvalidOperationException>(() => new CredentialProtector(config));
    }
}
