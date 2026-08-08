using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.TestingHost;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

public class ClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public static IInvalidationService InvalidationService { get; } = Substitute.For<IInvalidationService>();
    public static ICredentialProtector CredentialProtector { get; } =
        Substitute.For<ICredentialProtector>();

    public async Task InitializeAsync()
    {
        CredentialProtector.Protect(Arg.Any<string>())
            .Returns(call => $"protected:{call.Arg<string>()}");
        CredentialProtector.Unprotect(Arg.Any<string>())
            .Returns(call =>
            {
                var value = call.Arg<string>();
                if (!value.StartsWith("protected:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Credential was not protected");
                return value[10..];
            });
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await Cluster.StopAllSilosAsync();

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("postgres");
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton(InvalidationService);
                services.AddSingleton(CredentialProtector);
            });
        }
    }
}

[CollectionDefinition("Cluster")]
public class ClusterCollection : ICollectionFixture<ClusterFixture>;
