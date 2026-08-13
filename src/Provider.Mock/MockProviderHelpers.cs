using System.Buffers.Binary;
using System.Text.Json;

namespace ScalaAPI.Provider.Mock;

internal sealed record EmbeddingModelProfile(
    string Model,
    string Provider,
    int DefaultDimensions,
    int MaxDimensions,
    int CharactersPerToken);

internal static class MockProviderHelpers
{
    private const string ScenarioUserPrefix = "scalaapi-mock:";
    private static readonly IReadOnlyDictionary<string, EmbeddingModelProfile>
        EmbeddingProfiles = new Dictionary<string, EmbeddingModelProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["text-embedding-3-small"] = new(
                "text-embedding-3-small", "openai-compatible", 4, 8192, 4),
            ["jina-embeddings-v5-text-small"] = new(
                "jina-embeddings-v5-text-small", "jina-compatible", 8, 1024, 5),
            ["gemini-embedding-001"] = new(
                "gemini-embedding-001", "gemini-compatible", 6, 3072, 3),
        };

    private static readonly IReadOnlyDictionary<string, (string Provider, int CharactersPerToken)>
        ChatModelProfiles = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["grok-3"] = ("xai", 4),
            ["grok-3-mini"] = ("xai", 4),
            ["grok-2-image"] = ("xai", 4),
        };

    public static bool TryGetChatModelProfile(string model, out (string Provider, int CharactersPerToken) profile) =>
        ChatModelProfiles.TryGetValue(model.Trim(), out profile);

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

    public static bool TryGetEmbeddingProfile(string model,
        out EmbeddingModelProfile profile) =>
        EmbeddingProfiles.TryGetValue(model.Trim(), out profile!);

    public static IReadOnlyList<EmbeddingModelProfile> EmbeddingModelProfiles =>
        EmbeddingProfiles.Values.OrderBy(profile => profile.Model).ToArray();

    public static int EmbeddingDimensions(JsonElement root, int fallback = 4)
    {
        if (root.TryGetProperty("dimensions", out var dimensions)
            && dimensions.ValueKind == JsonValueKind.Number
            && dimensions.TryGetInt32(out var value))
            return value;
        return fallback;
    }

    public static string EmbeddingEncoding(JsonElement root) =>
        root.TryGetProperty("encoding_format", out var encoding)
        && encoding.ValueKind == JsonValueKind.String
            ? encoding.GetString() ?? "float"
            : "float";

    public static int EstimateEmbeddingInputTokens(JsonElement root,
        string model = "text-embedding-3-small")
    {
        if (!root.TryGetProperty("input", out var input)) return 0;
        var charactersPerToken = TryGetEmbeddingProfile(model, out var profile)
            ? profile.CharactersPerToken
            : 4;
        var total = input.ValueKind == JsonValueKind.Array
            ? input.EnumerateArray().Sum(value => EstimateStringTokens(value, charactersPerToken))
            : EstimateStringTokens(input, charactersPerToken);
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

    private static int EstimateStringTokens(JsonElement value, int charactersPerToken = 4) =>
        value.ValueKind == JsonValueKind.String
            ? Math.Clamp(((value.GetString()?.Length ?? 0) + charactersPerToken - 1)
                / charactersPerToken, 1, 4096)
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
