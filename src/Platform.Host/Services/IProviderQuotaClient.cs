using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public record ProviderQuotaInfo(
    string Tier,
    decimal? RemainingQuota,
    DateTime WindowStart,
    DateTime WindowEnd,
    DateTime ExpiresAt);

public interface IProviderQuotaClient
{
    string Platform { get; }
    Task<ProviderQuotaInfo?> GetQuotaAsync(AccountCredentials credentials, CancellationToken ct);
}
