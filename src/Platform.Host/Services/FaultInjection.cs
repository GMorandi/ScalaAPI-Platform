using System.Text;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Deterministic, opt-in process fault hooks for recovery verification.
/// Hooks are disabled unless FaultInjection:Hook is explicitly configured.
/// </summary>
public sealed class FaultInjection(
    IConfiguration configuration,
    ILogger<FaultInjection> logger)
{
    public bool TryClaim(string point, string correlation = "")
    {
        var configured = configuration["FaultInjection:Hook"];
        if (!string.Equals(configured, point, StringComparison.Ordinal))
            return false;

        if (configuration.GetValue("FaultInjection:Repeat", false))
            return true;

        var markerDirectory = configuration["FaultInjection:MarkerDirectory"];
        if (string.IsNullOrWhiteSpace(markerDirectory))
            markerDirectory = Path.Combine(Path.GetTempPath(), "scalaapi-fault-hooks");
        Directory.CreateDirectory(markerDirectory);

        var markerPath = Path.Combine(markerDirectory, Sanitize(point) + ".claimed");
        try
        {
            using var marker = new FileStream(
                markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 256, options: FileOptions.WriteThrough);
            var content = Encoding.UTF8.GetBytes(
                $"point={point}\ncorrelation={correlation}\npid={Environment.ProcessId}\n");
            marker.Write(content, 0, content.Length);
            marker.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void CrashIfConfigured(string point, string correlation = "")
    {
        if (!TryClaim(point, correlation))
            return;

        logger.LogCritical(
            "Fault injection claimed point {FaultPoint} for {Correlation}; terminating process",
            point, correlation);
        Environment.FailFast($"ScalaAPI fault injection: {point}");
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        return builder.ToString();
    }
}
