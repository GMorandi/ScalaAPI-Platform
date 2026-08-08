namespace ScalaAPI.Grains.Interfaces;

// Shared by scheduler and dispatch so that capability filtering happens before
// an account lease is created. Account-specific restrictions can narrow this
// baseline through model routing; this policy never broadens a provider.
public static class GatewayCapabilityPolicy
{
    public static bool Supports(string? platform, string? capability)
    {
        var provider = (platform ?? "").Trim().ToLowerInvariant();
        var feature = (capability ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(feature)) return false;
        return provider switch
        {
            "anthropic" or "claude" => feature is "messages" or "chat_completions"
                or "responses" or "responses_subpath" or "count_tokens" or "models"
                or "antigravity",
            "gemini" or "google" => feature is "gemini_models" or "gemini_generate"
                or "messages" or "chat_completions" or "responses",
            "antigravity" => feature is "antigravity" or "messages" or "count_tokens"
                or "gemini_models" or "gemini_generate" or "images_sync" or "images_async",
            "grok" or "xai" => feature is "chat_completions" or "responses"
                or "responses_subpath" or "models" or "videos" or "realtime",
            "openai" or "openai-compatible" => feature is "chat_completions" or "responses"
                or "responses_subpath" or "models" or "embeddings" or "images_sync"
                or "images_async" or "images_batch" or "search" or "realtime",
            _ => false,
        };
    }
}
