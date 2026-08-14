using System.Net.Http.Headers;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed class GeminiQuotaClient(HttpClient http, ILogger<GeminiQuotaClient> logger)
    : IProviderQuotaClient
{
    public string Platform => "gemini";

    public async Task<ProviderQuotaInfo?> GetQuotaAsync(AccountCredentials credentials, CancellationToken ct)
    {
        try
        {
            var baseUrl = credentials.BaseUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1beta/models");
            foreach (var (key, value) in credentials.AuthHeaders)
                request.Headers.TryAddWithoutValidation(key, value);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gemini quota check failed with status {Status}", response.StatusCode);
                return null;
            }

            var now = DateTime.UtcNow;
            var windowEnd = now.AddHours(1);

            return new ProviderQuotaInfo(
                Tier: "active",
                RemainingQuota: null,
                WindowStart: now,
                WindowEnd: windowEnd,
                ExpiresAt: windowEnd.AddMinutes(5));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gemini quota check failed for account {AccountId}", credentials.Id);
            return null;
        }
    }
}
