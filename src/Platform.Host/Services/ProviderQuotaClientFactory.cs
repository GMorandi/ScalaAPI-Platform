namespace ScalaAPI.Host.Services;

public sealed class ProviderQuotaClientFactory(IEnumerable<IProviderQuotaClient> clients)
{
    public IProviderQuotaClient? GetClient(string platform)
    {
        return clients.FirstOrDefault(c =>
            string.Equals(c.Platform, platform, StringComparison.OrdinalIgnoreCase));
    }
}
