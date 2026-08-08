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

        group.MapPost("/provider-mock-suite", async (IClusterClient client,
            ListingRepository registry) =>
        {
            var result = new List<object>();
            foreach (var profile in Profiles)
            {
                var accountId = await EnsureAccountAsync(client, registry, profile);
                var groupId = await EnsureGroupAsync(client, registry, profile, accountId);
                result.Add(new
                {
                    provider = profile.Platform,
                    account_id = accountId,
                    group_id = groupId,
                    models = profile.SupportedModels,
                });
            }
            return Results.Ok(new { providers = result });
        });

        group.MapPost("/provider-mock-fault-matrix", async (IClusterClient client,
            ListingRepository registry) =>
        {
            var result = new List<object>();
            foreach (var profile in FaultProfiles)
            {
                var accountId = await EnsureAccountAsync(client, registry, profile.Profile);
                var groupId = await EnsureGroupAsync(client, registry, profile.Profile, accountId);
                result.Add(new
                {
                    scenario = profile.Scenario,
                    account_id = accountId,
                    group_id = groupId,
                });
            }
            return Results.Ok(new { scenarios = result });
        });
    }

    private static async Task<long> EnsureAccountAsync(
        IClusterClient client, ListingRepository registry)
        => await EnsureAccountAsync(client, registry, Profiles[0]);

    private static async Task<long> EnsureAccountAsync(
        IClusterClient client, ListingRepository registry, MockProviderProfile profile)
    {
        var accountIds = await registry.GetIntegerGrainIds("account", 0, 1000);
        foreach (var accountId in accountIds)
        {
            var grain = client.GetGrain<IAccountGrain>(accountId);
            var projection = await grain.GetProjection();
            if (string.Equals(projection.Name, profile.Name, StringComparison.Ordinal))
            {
                await grain.Update(profile.Account());
                return accountId;
            }
        }

        var id = await client.GetGrain<IIdAllocatorGrain>("account").Next();
        await client.GetGrain<IAccountGrain>(id).Create(profile.Account());
        await registry.RegisterInteger("account", id);
        return id;
    }

    private static async Task<long> EnsureGroupAsync(
        IClusterClient client, ListingRepository registry, long accountId)
        => await EnsureGroupAsync(client, registry, Profiles[0], accountId);

    private static async Task<long> EnsureGroupAsync(
        IClusterClient client, ListingRepository registry, MockProviderProfile profile,
        long accountId)
    {
        var groupIds = await registry.GetIntegerGrainIds("group", 0, 1000);
        foreach (var groupId in groupIds)
        {
            var grain = client.GetGrain<IGroupGrain>(groupId);
            var config = await grain.GetConfig();
            var members = await grain.GetMemberAccountIds();
            if (string.Equals(config.Platform, profile.Platform, StringComparison.Ordinal)
                && members.Contains(accountId))
            {
                await grain.Update(profile.Group(accountId));
                return groupId;
            }
        }

        var id = await client.GetGrain<IIdAllocatorGrain>("group").Next();
        await client.GetGrain<IGroupGrain>(id).Create(profile.Group(accountId));
        await registry.RegisterInteger("group", id);
        return id;
    }

    private sealed record MockProviderProfile(string Name, string Platform,
        string[] SupportedModels)
    {
        public AccountUpsert Account() => new(
            Name, Platform, "api_key", "http://provider-mock:8081",
            Priority: 1, Concurrency: 8, LoadFactor: 1, RateMultiplier: 1m,
            Schedulable: true,
            Credentials: new Dictionary<string, string> { ["api_key"] = "scalaapi-mock-key" },
            ModelMapping: new Dictionary<string, string>(), SupportedModels,
            ProxyUrl: null, TlsFingerprint: false);

        public GroupUpsert Group(long accountId) => new(
            Platform, 1m, IsExclusive: false, DailyLimitUsd: null,
            ClaudeCodeOnly: false, FallbackGroupId: null, ModelRoutingEnabled: false,
            ModelRouting: new Dictionary<string, long[]>(), MemberAccountIds: [accountId],
            RpmLimit: 0, PeakMultiplier: null, PeakStartHour: null, PeakEndHour: null);
    }

    private static readonly MockProviderProfile[] Profiles =
    [
        new(ProviderName, "openai", ["gpt-4o", "text-embedding-3-small", "mock-image-1", "mock-video-1"]),
        new("scalaapi-provider-mock-anthropic", "anthropic", ["claude-3-5-sonnet"]),
        new("scalaapi-provider-mock-gemini", "gemini", ["gemini-2.0-flash"]),
    ];

    private static readonly (string Scenario, MockProviderProfile Profile)[] FaultProfiles =
    [
        ("429", new("scalaapi-provider-mock-fault-429", "openai", ["gpt-4o"])),
        ("500", new("scalaapi-provider-mock-fault-500", "openai", ["gpt-4o"])),
        ("timeout", new("scalaapi-provider-mock-fault-timeout", "openai", ["gpt-4o"])),
        ("disconnect", new("scalaapi-provider-mock-fault-disconnect", "openai", ["gpt-4o"])),
        ("client_disconnect", new("scalaapi-provider-mock-fault-client-disconnect", "openai", ["gpt-4o"])),
        ("malformed_usage", new("scalaapi-provider-mock-fault-malformed-usage", "openai", ["gpt-4o"])),
        ("invalid_content_type", new("scalaapi-provider-mock-fault-invalid-content-type", "openai", ["gpt-4o"])),
    ];
}
