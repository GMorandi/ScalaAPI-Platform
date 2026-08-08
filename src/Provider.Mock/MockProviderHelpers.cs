using System.Text.Json;

namespace ScalaAPI.Provider.Mock;

internal static class MockProviderHelpers
{
    private const string ScenarioUserPrefix = "scalaapi-mock:";

    public static async Task<JsonDocument> ReadJsonAsync(HttpContext context,
        CancellationToken cancellationToken)
    {
        return await JsonDocument.ParseAsync(context.Request.Body,
            cancellationToken: cancellationToken);
    }

    public static string Model(JsonElement root, string fallback = "gpt-4o") =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("model", out var model)
        && model.ValueKind == JsonValueKind.String
            ? model.GetString() ?? fallback
            : fallback;

    public static int EstimateInputTokens(JsonElement root)
    {
        var length = root.GetRawText().Length;
        return Math.Clamp((length + 3) / 4, 1, 4096);
    }

    public static string Scenario(HttpContext context, JsonElement root)
    {
        var scenario = context.Request.Headers["X-Provider-Scenario"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scenario))
            scenario = context.Request.Query["scenario"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scenario)
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("mock_scenario", out var scenarioValue)
            && scenarioValue.ValueKind == JsonValueKind.String)
            scenario = scenarioValue.GetString();
        if (string.IsNullOrWhiteSpace(scenario)
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("user", out var userValue)
            && userValue.ValueKind == JsonValueKind.String)
        {
            var user = userValue.GetString();
            if (user?.StartsWith(ScenarioUserPrefix, StringComparison.Ordinal) == true)
                scenario = user[ScenarioUserPrefix.Length..];
        }
        return string.IsNullOrWhiteSpace(scenario) ? "success" : scenario.Trim();
    }

    public static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    public static string OutputUrl(string id) =>
        $"http://provider-mock:8081/v1/mock-output/{Uri.EscapeDataString(id)}";
}
