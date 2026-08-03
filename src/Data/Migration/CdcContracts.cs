using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sub2Api.Data.Migration;

public static class CdcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record ChangeEnvelope(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("epoch")] long Epoch,
    [property: JsonPropertyName("source_lsn")] string SourceLsn,
    [property: JsonPropertyName("transaction_id")] string TransactionId,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] string AggregateId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("payload_hash")] string PayloadHash,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    // Debezium emits "true", "last", or "false" in source.snapshot. It is
    // optional so canonical envelopes and semantic outbox records remain valid.
    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; init; }

    private static readonly HashSet<string> RestrictedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "api_key", "credentials", "password", "password_hash",
        "registration_password_hash", "totp_secret_encrypted", "access_token",
        "refresh_token", "session_key", "client_secret", "proxy_password",
        "private_key", "secret_key"
    };

    public void Validate()
    {
        if (!Guid.TryParse(EventId, out _)) throw new FormatException("event_id must be a UUID");
        if (Epoch <= 0) throw new FormatException("epoch must be positive");
        if (string.IsNullOrWhiteSpace(SourceLsn)) throw new FormatException("source_lsn is required");
        if (string.IsNullOrWhiteSpace(TransactionId)) throw new FormatException("transaction_id is required");
        if (string.IsNullOrWhiteSpace(AggregateType) || string.IsNullOrWhiteSpace(AggregateId))
            throw new FormatException("aggregate identity is required");
        if (Operation is not ("insert" or "update" or "delete" or "snapshot"))
            throw new FormatException($"unsupported operation: {Operation}");
        if (SchemaVersion != 1) throw new FormatException($"unsupported schema_version: {SchemaVersion}");
        if (string.IsNullOrWhiteSpace(PayloadHash)) throw new FormatException("payload_hash is required");
        if (Snapshot is not null && Snapshot is not ("first" or "true" or "last_in_data_collection" or "last" or "false"))
            throw new FormatException($"unsupported snapshot marker: {Snapshot}");
        EnsureNoRestrictedFields(Payload);
    }

    public static string ComputePayloadHash(JsonElement payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CdcJson.Options));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static bool TryParseLsn(string value, out decimal position)
    {
        position = 0;
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var decimalPosition)
            && decimalPosition >= 0)
        {
            position = decimalPosition;
            return true;
        }
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var upper)
            || !ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var lower))
            return false;
        position = ((decimal)upper * 4_294_967_296m) + lower;
        return true;
    }

    private void EnsureNoRestrictedFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (RestrictedFieldNames.Contains(property.Name)
                    || (string.Equals(AggregateType, "account", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(property.Name, "extra", StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException($"restricted field is forbidden in ordinary CDC: {property.Name}");
                EnsureNoRestrictedFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) EnsureNoRestrictedFields(item);
        }
    }
}

public sealed record SyncAck(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("epoch")] long Epoch,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] string AggregateId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("acked_at")] DateTimeOffset AckedAt);

/// <summary>
/// Restricted account credential envelope. This contract is transported on a
/// separate ACL-protected topic and is never serialized as ChangeEnvelope.
/// Ciphertext is an <c>enc:v1:</c> payload encrypted for the target key version.
/// </summary>
public sealed record CredentialEnvelope(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("epoch")] long Epoch,
    [property: JsonPropertyName("source_lsn")] string SourceLsn,
    [property: JsonPropertyName("transaction_id")] string TransactionId,
    [property: JsonPropertyName("aggregate_type")] string AggregateType,
    [property: JsonPropertyName("aggregate_id")] string AggregateId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("key_version")] string KeyVersion,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("payload_hash")] string PayloadHash,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt)
{
    public void Validate()
    {
        if (!Guid.TryParse(EventId, out _)) throw new FormatException("credential event_id must be a UUID");
        if (Epoch <= 0 || string.IsNullOrWhiteSpace(SourceLsn)
            || string.IsNullOrWhiteSpace(TransactionId))
            throw new FormatException("credential ordering fields are required");
        if (!string.Equals(AggregateType, "account", StringComparison.Ordinal))
            throw new FormatException("credential aggregate_type must be account");
        if (string.IsNullOrWhiteSpace(AggregateId) || !long.TryParse(AggregateId, out _))
            throw new FormatException("credential aggregate_id must be numeric");
        if (Operation is not ("insert" or "update" or "delete" or "snapshot"))
            throw new FormatException($"unsupported credential operation: {Operation}");
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(KeyVersion))
            throw new FormatException("unsupported or missing credential key version");
        if (!Ciphertext.StartsWith("enc:v1:", StringComparison.Ordinal))
            throw new FormatException("credential ciphertext must use enc:v1");
        try
        {
            _ = Convert.FromBase64String(Ciphertext["enc:v1:".Length..]);
        }
        catch (FormatException ex)
        {
            throw new FormatException("credential ciphertext is not base64", ex);
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(PayloadHash, "^[0-9a-fA-F]{64}$"))
            throw new FormatException("credential payload_hash must be SHA-256 hex");
    }
}

public sealed record MigrationFence(
    long Epoch,
    string WritePrimary,
    string Mode,
    string Reason,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public static class DebeziumEnvelopeAdapter
{
    public static ChangeEnvelope Adapt(JsonElement root, long epoch)
    {
        var eventBody = root;
        if (root.TryGetProperty("payload", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object
            && wrapped.TryGetProperty("op", out _))
            eventBody = wrapped;

        if (!eventBody.TryGetProperty("op", out var opValue))
            throw new FormatException("message is neither ChangeEnvelope v1 nor a Debezium PostgreSQL record");
        var source = eventBody.TryGetProperty("source", out var sourceValue)
            && sourceValue.ValueKind == JsonValueKind.Object ? sourceValue : default;
        var table = Text(source, "table");
        if (table == "migration_cdc_outbox")
            return AdaptSemanticOutbox(eventBody, source, epoch);
        var aggregateType = table switch
        {
            "users" => "user",
            "api_keys" => "api_key",
            "groups" => "group",
            "accounts" => "account",
            "usage_logs" => "usage",
            _ => table
        };
        if (string.IsNullOrWhiteSpace(aggregateType)) throw new FormatException("Debezium source.table is required");

        var operation = opValue.GetString() switch
        {
            "c" => "insert",
            "u" => "update",
            "d" => "delete",
            "r" => "snapshot",
            var op => throw new FormatException($"unsupported Debezium operation: {op}")
        };
        var payload = eventBody.TryGetProperty("after", out var after) && after.ValueKind != JsonValueKind.Null
            ? after.Clone()
            : eventBody.TryGetProperty("before", out var before) && before.ValueKind != JsonValueKind.Null
                ? before.Clone() : throw new FormatException("Debezium record has no before/after payload");
        var lsn = Text(source, "lsn");
        var tx = Text(source, "txId");
        if (string.IsNullOrWhiteSpace(lsn)) lsn = $"snapshot:{Text(source, "ts_ms")}:{aggregateType}";
        if (string.IsNullOrWhiteSpace(tx)) tx = lsn;
        var aggregateId = Id(payload, aggregateType);
        var payloadHash = ChangeEnvelope.ComputePayloadHash(payload);
        var eventId = DeterministicGuid($"{lsn}|{tx}|{aggregateType}|{aggregateId}|{operation}|{payloadHash}").ToString();
        return new ChangeEnvelope(eventId, epoch, lsn, tx, aggregateType, aggregateId,
            operation, 1, OccurredAt(source), payloadHash, payload)
        {
            Snapshot = SnapshotMarker(source)
        };
    }

    private static ChangeEnvelope AdaptSemanticOutbox(JsonElement eventBody,
        JsonElement source, long epoch)
    {
        if (!eventBody.TryGetProperty("after", out var row) || row.ValueKind != JsonValueKind.Object)
            throw new FormatException("semantic outbox event requires an after row");
        var eventId = Text(row, "event_id");
        var aggregateType = Text(row, "aggregate_type");
        var aggregateId = Text(row, "aggregate_id");
        var operation = Text(row, "operation");
        if (!row.TryGetProperty("payload", out var payload)
            || (payload.ValueKind != JsonValueKind.Object && payload.ValueKind != JsonValueKind.String))
            throw new FormatException("semantic outbox payload is required");
        JsonElement clonedPayload;
        if (payload.ValueKind == JsonValueKind.String)
        {
            var serialized = payload.GetString();
            if (string.IsNullOrWhiteSpace(serialized))
                throw new FormatException("semantic outbox payload string is empty");
            using var parsed = JsonDocument.Parse(serialized);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("semantic outbox payload string must contain an object");
            clonedPayload = parsed.RootElement.Clone();
        }
        else
        {
            clonedPayload = payload.Clone();
        }
        var lsn = Text(source, "lsn");
        var tx = Text(source, "txId");
        if (string.IsNullOrWhiteSpace(lsn)) lsn = $"snapshot:{Text(source, "ts_ms")}:migration_cdc_outbox";
        if (string.IsNullOrWhiteSpace(tx)) tx = lsn;
        return new ChangeEnvelope(eventId, epoch, lsn, tx, aggregateType, aggregateId,
            operation, 1, OccurredAt(source), ChangeEnvelope.ComputePayloadHash(clonedPayload), clonedPayload)
        {
            Snapshot = SnapshotMarker(source)
        };
    }

    private static string Id(JsonElement payload, string aggregateType)
    {
        if (payload.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
            return id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
        if (aggregateType is "account_groups" or "user_allowed_groups"
            && payload.TryGetProperty(aggregateType == "account_groups" ? "account_id" : "user_id", out var owner)
            && payload.TryGetProperty("group_id", out var group))
            return $"{owner.GetRawText()}:{group.GetRawText()}";
        throw new FormatException($"Debezium {aggregateType} record has no aggregate id");
    }

    private static DateTimeOffset OccurredAt(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("ts_ms", out var timestamp)
            && timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unixMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        return DateTimeOffset.UtcNow;
    }

    private static string? SnapshotMarker(JsonElement source)
    {
        var marker = Text(source, "snapshot");
        return marker is "first" or "true" or "last_in_data_collection" or "last" or "false"
            ? marker : null;
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
