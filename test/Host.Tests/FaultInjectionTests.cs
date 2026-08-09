using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ScalaAPI.Host.Services;

namespace ScalaAPI.Host.Tests;

public sealed class FaultInjectionTests
{
    [Fact]
    public void ClaimsConfiguredPointOnlyOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scalaapi-fault-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FaultInjection:Hook"] = "platform.after_settlement_commit",
                    ["FaultInjection:MarkerDirectory"] = directory,
                })
                .Build();
            var faults = new FaultInjection(configuration, NullLogger<FaultInjection>.Instance);

            Assert.True(faults.TryClaim("platform.after_settlement_commit", "lease-1"));
            Assert.False(faults.TryClaim("platform.after_settlement_commit", "lease-2"));
            Assert.False(faults.TryClaim("platform.before_settlement_commit", "lease-3"));
            Assert.Equal(
                "point=platform.after_settlement_commit",
                File.ReadLines(Path.Combine(directory, "platform-after-settlement-commit.claimed")).First());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepeatModeClaimsEveryAttempt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FaultInjection:Hook"] = "platform.before_provider_dispatch",
                ["FaultInjection:Repeat"] = "true",
            })
            .Build();
        var faults = new FaultInjection(configuration, NullLogger<FaultInjection>.Instance);

        Assert.True(faults.TryClaim("platform.before_provider_dispatch", "request-1"));
        Assert.True(faults.TryClaim("platform.before_provider_dispatch", "request-2"));
    }

    [Fact]
    public void ClaimsAfterOutboxClaimPointOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scalaapi-fault-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FaultInjection:Hook"] = "platform.after_outbox_claim",
                    ["FaultInjection:MarkerDirectory"] = directory,
                })
                .Build();
            var faults = new FaultInjection(configuration, NullLogger<FaultInjection>.Instance);

            Assert.True(faults.TryClaim("platform.after_outbox_claim", "lease-1"));
            Assert.False(faults.TryClaim("platform.after_outbox_claim", "lease-2"));
            Assert.Equal(
                "point=platform.after_outbox_claim",
                File.ReadLines(Path.Combine(directory, "platform-after-outbox-claim.claimed")).First());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
