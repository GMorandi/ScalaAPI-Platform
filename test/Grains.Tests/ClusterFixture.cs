using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.TestingHost;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains.Tests;

public class ClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public static IInvalidationService InvalidationService { get; } = Substitute.For<IInvalidationService>();

    public async Task InitializeAsync()
    {
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
                services.AddSingleton(InvalidationService));
        }
    }
}

[CollectionDefinition("Cluster")]
public class ClusterCollection : ICollectionFixture<ClusterFixture>;
