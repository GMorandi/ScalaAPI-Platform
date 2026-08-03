using System.Globalization;
using System.Text.Json;
using Orleans;
using Sub2Api.Data.Migration;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Host.Services;

public sealed class CdcGrainApplier(
    IGrainFactory grains,
    IInvalidationService invalidation,
    ILogger<CdcGrainApplier> logger)
{
    public async Task ApplyAsync(ChangeEnvelope envelope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = envelope.Payload;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("after", out var after)
            && after.ValueKind == JsonValueKind.Object)
            payload = after;

        switch (envelope.AggregateType)
        {
            case "user":
            case "user_account":
                await ApplyUserAsync(envelope, payload);
                break;
            case "group":
                await ApplyGroupAsync(envelope, payload);
                break;
            case "api_key":
            case "user_api_key":
                await ApplyApiKeyAsync(envelope, payload);
                break;
            case "usage":
            case "usage_log":
                await ApplyUsageAsync(envelope, payload);
                break;
            case "account":
                await ApplyAccountMetadataAsync(envelope, payload);
                break;
            case "account_groups":
                await ApplyAccountGroupAsync(envelope, payload);
                break;
            case "user_allowed_groups":
                await ApplyUserAllowedGroupAsync(envelope, payload);
                break;
            case "auth_cache_invalidation_outbox":
                invalidation.NotifyChange("apiKey", Text(payload, "cache_key"));
                break;
            case "scheduler_outbox":
                ApplySchedulerInvalidation(payload);
                break;
            default:
                logger.LogDebug("CDC event {EventId} for non-core aggregate {AggregateType} retained in inbox",
                    envelope.EventId, envelope.AggregateType);
                break;
        }
    }

    private async Task ApplyUserAsync(ChangeEnvelope envelope, JsonElement p)
    {
        var id = Id(envelope, p);
        var grain = grains.GetGrain<IUserGrain>(id);
        if (envelope.Operation == "delete")
        {
            await grain.Delete();
            return;
        }
        await grain.UpsertMetadata(new UserMetadataUpsert(
            Text(p, "role", "user"), Number(p, "balance"), Int(p, "concurrency", 1),
            Int(p, "rpm_limit")));
        if (p.TryGetProperty("status", out var status)) await grain.SetStatus(status.GetString() ?? "active");
    }

    private async Task ApplyGroupAsync(ChangeEnvelope envelope, JsonElement p)
    {
        var grain = grains.GetGrain<IGroupGrain>(Id(envelope, p));
        if (envelope.Operation == "delete")
        {
            await grain.Delete();
            return;
        }
        await grain.UpsertMetadata(new GroupMetadataUpsert(
            Text(p, "platform", "anthropic"), Number(p, "rate_multiplier", 1), Bool(p, "is_exclusive"),
            NullableNumber(p, "daily_limit_usd"), Bool(p, "claude_code_only"), NullableLong(p, "fallback_group_id"),
            Bool(p, "model_routing_enabled"), LongMap(p, "model_routing"), Int(p, "rpm_limit"),
            NullableNumber(p, "peak_rate_multiplier") ?? NullableNumber(p, "peak_multiplier"),
            NullableInt(p, "peak_start_hour"), NullableInt(p, "peak_end_hour")));
        if (p.TryGetProperty("status", out var status)) await grain.SetStatus(status.GetString() ?? "active");
    }

    private async Task ApplyApiKeyAsync(ChangeEnvelope envelope, JsonElement p)
    {
        var grain = grains.GetGrain<IApiKeyGrain>(envelope.AggregateId);
        if (envelope.Operation == "delete")
        {
            await grain.Revoke();
            return;
        }
        var input = new ApiKeyUpsert(
            Long(p, "user_id"), Long(p, "group_id"), Number(p, "quota"), NullableUnixMilliseconds(p, "expires_at"),
            StringArray(p, "ip_whitelist"), StringArray(p, "ip_blacklist"), Number(p, "rate_limit_5h"),
            Number(p, "rate_limit_1d"), Number(p, "rate_limit_7d"));
        // Semantic API-key events use the hashed Gateway key as aggregate_id;
        // the numeric source ID is carried in the payload. Avoid evaluating a
        // numeric fallback eagerly, because that would reject the valid hash.
        var keyId = Long(p, "api_key_id");
        if (keyId <= 0) keyId = ParseLong(envelope.AggregateId);
        if (envelope.Operation is "insert" or "snapshot") await grain.Create(input, keyId);
        else await grain.Update(input);
        if (p.TryGetProperty("status", out var status) && !string.Equals(status.GetString(), "active", StringComparison.OrdinalIgnoreCase))
            await grain.Revoke();
    }

    private async Task ApplyUsageAsync(ChangeEnvelope envelope, JsonElement p)
    {
        if (envelope.Operation == "delete") return;
        await grains.GetGrain<IUsageGrain>(envelope.AggregateId).Record(new UsageEventData(
            Text(p, "lease_token", envelope.AggregateId), Text(p, "request_id", envelope.AggregateId),
            Long(p, "api_key_id"), Long(p, "user_id"), Long(p, "account_id"), Long(p, "group_id"),
            Text(p, "model"), Text(p, "upstream_model"), Int(p, "input_tokens"), Int(p, "output_tokens"),
            Int(p, "cache_create_tokens"), Int(p, "cache_read_tokens"), Int(p, "duration_ms"),
            Int(p, "first_token_ms"), Bool(p, "stream"), Bool(p, "client_disconnect")));
    }

    private async Task ApplyAccountMetadataAsync(ChangeEnvelope envelope, JsonElement p)
    {
        if (p.TryGetProperty("credentials", out _))
            throw new InvalidOperationException("account credentials must use the restricted encrypted channel");
        var grain = grains.GetGrain<IAccountGrain>(Id(envelope, p));
        if (envelope.Operation == "delete")
        {
            await grain.Delete();
            return;
        }
        await grain.UpsertMetadata(new AccountMetadataUpsert(
            Text(p, "name"), Text(p, "platform"), Text(p, "type"), Text(p, "base_url"),
            Int(p, "priority", 50), Int(p, "concurrency", 1), Int(p, "load_factor", 1),
            Number(p, "rate_multiplier", 1), Bool(p, "schedulable", true),
            StringMap(p, "model_mapping"), StringArray(p, "supported_models"),
            NullableText(p, "proxy_url"), Bool(p, "tls_fingerprint")));
        if (p.TryGetProperty("status", out var status)) await grain.SetStatus(status.GetString() ?? "active");
    }

    private async Task ApplyAccountGroupAsync(ChangeEnvelope envelope, JsonElement p)
    {
        var group = grains.GetGrain<IGroupGrain>(Long(p, "group_id"));
        if (envelope.Operation == "delete") await group.RemoveMemberAccount(Long(p, "account_id"));
        else await group.AddMemberAccount(Long(p, "account_id"));
    }

    private async Task ApplyUserAllowedGroupAsync(ChangeEnvelope envelope, JsonElement p)
    {
        var user = grains.GetGrain<IUserGrain>(Long(p, "user_id"));
        if (envelope.Operation == "delete") await user.RemoveAllowedGroup(Long(p, "group_id"));
        else await user.AddAllowedGroup(Long(p, "group_id"));
    }

    private void ApplySchedulerInvalidation(JsonElement p)
    {
        if (p.TryGetProperty("account_id", out var account) && account.ValueKind != JsonValueKind.Null)
            invalidation.NotifyChange("account", account.GetInt64().ToString(CultureInfo.InvariantCulture));
        if (p.TryGetProperty("group_id", out var group) && group.ValueKind != JsonValueKind.Null)
            invalidation.NotifyChange("group", group.GetInt64().ToString(CultureInfo.InvariantCulture));
    }

    private static long Id(ChangeEnvelope e, JsonElement p) => Long(p, "id", ParseLong(e.AggregateId));
    private static long ParseLong(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : throw new FormatException("aggregate_id must be numeric");
    private static long Long(JsonElement p, string name, long fallback = 0) =>
        p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? ParseLong(v, name, fallback) : fallback;
    private static int Int(JsonElement p, string name, int fallback = 0) =>
        p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (int)ParseLong(v, name, fallback) : fallback;
    private static double Number(JsonElement p, string name, double fallback = 0) =>
        p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? ParseDouble(v, name, fallback) : fallback;
    private static double? NullableNumber(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? ParseDouble(v, name, 0) : null;
    private static int? NullableInt(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (int)ParseLong(v, name, 0) : null;
    private static long? NullableLong(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? ParseLong(v, name, 0) : null;
    private static long? NullableUnixMilliseconds(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        var text = v.GetString();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs))
            return unixMs;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUnixTimeMilliseconds() : null;
    }
    private static bool Bool(JsonElement p, string name, bool fallback = false) => p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetBoolean() : fallback;
    private static string Text(JsonElement p, string name, string fallback = "") => p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() ?? fallback : fallback;
    private static string? NullableText(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    private static long ParseLong(JsonElement value, string name, long fallback)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return fallback;
    }

    private static double ParseDouble(JsonElement value, string name, double fallback)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        return fallback;
    }
    private static Dictionary<string, string> StringMap(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
        ? v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "") : new();
    private static string[] StringArray(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
    private static long[] LongArray(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetInt64()).ToArray() : [];
    private static Dictionary<string, long[]> LongMap(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
        ? v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.EnumerateArray().Select(y => y.GetInt64()).ToArray()) : new();
}
