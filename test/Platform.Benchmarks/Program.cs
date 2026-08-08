using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

try
{
    var summaries = BenchmarkSwitcher
        .FromAssembly(typeof(ScalaAPI.Platform.Benchmarks.SchedulerBenchmarks).Assembly)
        .Run(args)
        .ToArray();
    if (summaries.Length == 0 || summaries.Any(summary =>
            summary.HasCriticalValidationErrors ||
            !summary.Reports.Any() ||
            summary.Reports.Any(report => !report.Success)))
        Environment.ExitCode = 1;
}
catch
{
    Environment.ExitCode = 1;
    throw;
}
