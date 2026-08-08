using System.Text.Json;

namespace ScalaAPI.Provider.Mock;

internal static class MockProviderHelpers
{
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

    public static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    public static string OutputUrl(string id) =>
        $"http://provider-mock:8081/v1/mock-output/{Uri.EscapeDataString(id)}";
}
