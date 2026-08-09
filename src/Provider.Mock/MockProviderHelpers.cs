using System.Buffers.Binary;
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

    public static int EmbeddingInputCount(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input)) return 0;
        return input.ValueKind == JsonValueKind.Array ? input.GetArrayLength() : 1;
    }

    public static int EmbeddingDimensions(JsonElement root)
    {
        if (root.TryGetProperty("dimensions", out var dimensions)
            && dimensions.ValueKind == JsonValueKind.Number
            && dimensions.TryGetInt32(out var value))
            return value;
        return 4;
    }

    public static string EmbeddingEncoding(JsonElement root) =>
        root.TryGetProperty("encoding_format", out var encoding)
        && encoding.ValueKind == JsonValueKind.String
            ? encoding.GetString() ?? "float"
            : "float";

    public static int EstimateEmbeddingInputTokens(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input)) return 0;
        var total = input.ValueKind == JsonValueKind.Array
            ? input.EnumerateArray().Sum(EstimateStringTokens)
            : EstimateStringTokens(input);
        return Math.Clamp(total, 1, 4096);
    }

    public static double[] EmbeddingValues(int inputIndex, int dimensions) =>
        Enumerable.Range(0, dimensions)
            .Select(dimension => Math.Round(
                0.125 + ((inputIndex * 31 + dimension * 17) % 100) / 100.0, 6))
            .ToArray();

    public static string EmbeddingBase64(int inputIndex, int dimensions)
    {
        var bytes = new byte[checked(dimensions * sizeof(float))];
        var values = EmbeddingValues(inputIndex, dimensions);
        for (var offset = 0; offset < values.Length; offset++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset * sizeof(float)),
                BitConverter.SingleToInt32Bits((float)values[offset]));
        return Convert.ToBase64String(bytes);
    }

    private static int EstimateStringTokens(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? Math.Clamp(((value.GetString()?.Length ?? 0) + 3) / 4, 1, 4096)
            : 0;

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
