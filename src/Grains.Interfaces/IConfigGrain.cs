namespace Sub2Api.Grains.Interfaces;

public interface IConfigGrain : IGrainWithStringKey
{
    Task<Dictionary<string, string>> Get();
    Task Update(string key, string value);
    Task<long> GetVersion();
}
