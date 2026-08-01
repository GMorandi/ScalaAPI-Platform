using BenchmarkDotNet.Attributes;
using NSubstitute;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Platform.Benchmarks;

[MemoryDiagnoser]
public class DispatchBenchmarks
{
    private IInvalidationService _service = null!;

    [GlobalSetup]
    public void Setup()
    {
        _service = Substitute.For<IInvalidationService>();
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public void NotifyChange_Throughput()
    {
        for (int i = 0; i < 1000; i++)
            _service.NotifyChange("apiKey", i.ToString());
    }

    [Benchmark]
    public void NotifyChange_Single()
    {
        _service.NotifyChange("user", "12345");
    }
}
