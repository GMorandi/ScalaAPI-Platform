using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.TestingHost;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Platform.Benchmarks;

[MemoryDiagnoser]
public class SchedulerBenchmarks
{
    private TestCluster _cluster = null!;
    private ISchedulerGrain _scheduler = null!;

    [Params(10, 100)]
    public int AccountCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfig>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        var groupId = 9000L + AccountCount;
        var accountIds = Enumerable.Range(1, AccountCount).Select(i => (long)(8000 + i)).ToArray();

        var groupGrain = _cluster.GrainFactory.GetGrain<IGroupGrain>(groupId);
        await groupGrain.Create(new GroupUpsert(
            "anthropic", 1.0m, false, null, false, null, false, new(), accountIds, 0, null, null, null));

        foreach (var id in accountIds)
        {
            var acct = _cluster.GrainFactory.GetGrain<IAccountGrain>(id);
            await acct.Create(new AccountUpsert(
                $"acct-{id}", "anthropic", "api_key", "https://api.example.com",
                1, 100, 100, 1.0m, true, new(), new(), [], null, false));
        }

        _scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(groupId);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _cluster.StopAllSilosAsync();

    [Benchmark]
    public async Task<SelectionResult> SelectColdPath()
    {
        var hash = Guid.NewGuid().ToString();
        return await _scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", hash, Guid.NewGuid().ToString(), null, [], "/v1/messages"));
    }

    [Benchmark]
    public async Task<SelectionResult> SelectStickyHit()
    {
        return await _scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", "fixed-session", Guid.NewGuid().ToString(), null, [], "/v1/messages"));
    }

    private class SiloConfig : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("postgres");
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IInvalidationService>());
                services.AddSingleton(Substitute.For<ICredentialProtector>());
                services.AddSingleton<ISlotLeaseStore, InMemorySlotLeaseStore>();
            });
        }
    }
}
