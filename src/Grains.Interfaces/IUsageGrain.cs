namespace Sub2Api.Grains.Interfaces;

public record UsageEventData(
    string LeaseToken, string RequestId, long ApiKeyId, long UserId,
    long AccountId, long GroupId, string Model, string UpstreamModel,
    int InputTokens, int OutputTokens, int CacheCreateTokens,
    int CacheReadTokens, int DurationMs, int FirstTokenMs,
    bool Stream, bool ClientDisconnect);

public interface IUsageGrain : IGrainWithStringKey
{
    Task Record(UsageEventData e);
    Task Flush();
}
