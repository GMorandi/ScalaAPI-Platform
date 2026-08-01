using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Sub2Api.Platform.Benchmarks.SchedulerBenchmarks).Assembly).Run(args);
