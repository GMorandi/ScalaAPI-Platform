using Orleans;
using ScalaAPI.Admin.Data;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public static class SeedEndpoints
{
    private const string ProviderName = "scalaapi-provider-mock";

    public static void MapSeedEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/seed").RequireAuthorization("AdminOnly");
        group.MapPost("/provider-mock", async (IClusterClient client, ListingRepository registry) =>
        {
            var accountId = await EnsureAccountAsync(client, registry);
            var groupId = await EnsureGroupAsync(client, registry, accountId);
            return Results.Ok(new
            {
                provider = "mock",
                account_id = accountId,
                group_id = groupId,
                model = "gpt-4o",
            });
        });
    }

    private static async Task<long> EnsureAccountAsync(
        IClusterClient client, ListingRepository registry)
    {
        var accountIds = await registry.GetIntegerGrainIds("account", 0, 1000);
        foreach (var accountId in accountIds)
        {
            var grain = client.GetGrain<IAccountGrain>(accountId);
            var projection = await grain.GetProjection();
            if (string.Equals(projection.Name, ProviderName, StringComparison.Ordinal))
            {
                await grain.Update(MockAccount());
                return accountId;
            }
        }

        var id = await client.GetGrain<IIdAllocatorGrain>("account").Next();
        await client.GetGrain<IAccountGrain>(id).Create(MockAccount());
        await registry.RegisterInteger("account", id);
        return id;
    }

    private static async Task<long> EnsureGroupAsync(
        IClusterClient client, ListingRepository registry, long accountId)
    {
        var groupIds = await registry.GetIntegerGrainIds("group", 0, 1000);
        foreach (var groupId in groupIds)
        {
            var grain = client.GetGrain<IGroupGrain>(groupId);
            var config = await grain.GetConfig();
            var members = await grain.GetMemberAccountIds();
            if (config.Platform == "openai" && members.Contains(accountId))
            {
                await grain.Update(MockGroup(accountId));
                return groupId;
            }
        }

        var id = await client.GetGrain<IIdAllocatorGrain>("group").Next();
        await client.GetGrain<IGroupGrain>(id).Create(MockGroup(accountId));
        await registry.RegisterInteger("group", id);
        return id;
    }

    private static AccountUpsert MockAccount() => new(
        ProviderName, "openai", "api_key", "http://provider-mock:8081",
        Priority: 1, Concurrency: 8, LoadFactor: 1, RateMultiplier: 1m,
        Schedulable: true,
        Credentials: new Dictionary<string, string> { ["api_key"] = "scalaapi-mock-key" },
        ModelMapping: new Dictionary<string, string>(),
        SupportedModels: ["gpt-4o"], ProxyUrl: null, TlsFingerprint: false);

    private static GroupUpsert MockGroup(long accountId) => new(
        "openai", 1m, IsExclusive: false, DailyLimitUsd: null,
        ClaudeCodeOnly: false, FallbackGroupId: null, ModelRoutingEnabled: false,
        ModelRouting: new Dictionary<string, long[]>(), MemberAccountIds: [accountId],
        RpmLimit: 0, PeakMultiplier: null, PeakStartHour: null, PeakEndHour: null);
}
