using System.Text;
using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data;

public sealed record AuditLogView(
    long Id,
    long ActorUserId,
    string Action,
    string? ResourceType,
    string? ResourceId,
    string? Details,
    string? IpAddress,
    DateTime CreatedAt);

public sealed record AuditLogPage(
    IReadOnlyList<AuditLogView> Items,
    long Total,
    int Page,
    int Size);

public sealed class AuditLogStore(NpgsqlDataSource dataSource)
{
    public async Task<AuditLogPage> ListAsync(
        long? userId,
        string? action,
        DateTime? from,
        DateTime? to,
        int page,
        int size,
        int maximumSize = 100,
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 10_000);
        size = Math.Clamp(size, 1, Math.Clamp(maximumSize, 1, 1_000));
        action = string.IsNullOrWhiteSpace(action) ? null : action.Trim();

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT count(*)
            FROM audit_logs
            WHERE ($1::bigint IS NULL OR user_id = $1)
              AND ($2::text IS NULL OR action = $2)
              AND ($3::timestamptz IS NULL OR created_at >= $3)
              AND ($4::timestamptz IS NULL OR created_at <= $4)
            """;
        AddFilters(count, userId, action, from, to);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));

        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT id, user_id, action, resource_type, resource_id, details,
                   ip_address, created_at
            FROM audit_logs
            WHERE ($1::bigint IS NULL OR user_id = $1)
              AND ($2::text IS NULL OR action = $2)
              AND ($3::timestamptz IS NULL OR created_at >= $3)
              AND ($4::timestamptz IS NULL OR created_at <= $4)
            ORDER BY created_at DESC, id DESC
            OFFSET $5 LIMIT $6
            """;
        AddFilters(query, userId, action, from, to);
        query.Parameters.AddWithValue((page - 1) * size);
        query.Parameters.AddWithValue(size);

        var items = new List<AuditLogView>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new AuditLogView(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                RedactDetails(reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetDateTime(7)));
        }
        return new(items, total, page, size);
    }

    private static void AddFilters(
        NpgsqlCommand command,
        long? userId,
        string? action,
        DateTime? from,
        DateTime? to)
    {
        command.Parameters.AddWithValue((object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)action ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)to ?? DBNull.Value);
    }

    private static string? RedactDetails(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return details;
        try
        {
            using var document = JsonDocument.Parse(details);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRedacted(document.RootElement, writer);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return "[redacted: non-json audit details]";
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitive(property.Name))
                        writer.WriteStringValue("[redacted]");
                    else
                        WriteRedacted(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray())
                    WriteRedacted(child, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name)
    {
        return name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("key", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_key", StringComparison.OrdinalIgnoreCase);
    }
}
