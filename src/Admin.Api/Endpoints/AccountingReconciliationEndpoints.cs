using System.Text.Json;
using Npgsql;
using ScalaAPI.Data.Accounting;

namespace ScalaAPI.Admin.Endpoints;

public static class AccountingReconciliationEndpoints
{
    public static void MapAccountingReconciliationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/reconciliation")
            .RequireAuthorization("AdminOnly");

        group.MapPost("/run", async (
            AccountingReconciliationService reconciliation,
            CancellationToken ct) =>
        {
            var result = await reconciliation.RunAsync("admin", ct);
            return result.Started ? Results.Ok(result) : Results.Conflict(result);
        });

        group.MapGet("/runs", async (
            NpgsqlDataSource dataSource,
            string? status,
            int page = 1,
            int size = 50,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 200);
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var where = string.IsNullOrWhiteSpace(status) ? "" : "WHERE status = $1";
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM ledger_reconciliation_runs {where}";
            if (where.Length > 0) count.Parameters.AddWithValue(status!.Trim());
            var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));

            await using var command = connection.CreateCommand();
            var filterParameterCount = where.Length > 0 ? 1 : 0;
            command.CommandText = $"""
                SELECT id, started_at, completed_at, status, ledger_total, hold_total,
                       mismatch_total, checked_accounts, repaired_holds,
                       repaired_projections, open_incidents, resolved_incidents,
                       details::text
                FROM ledger_reconciliation_runs
                {where}
                ORDER BY id DESC
                LIMIT ${filterParameterCount + 1}
                OFFSET ${filterParameterCount + 2}
                """;
            if (where.Length > 0) command.Parameters.AddWithValue(status!.Trim());
            command.Parameters.AddWithValue(size);
            command.Parameters.AddWithValue((page - 1) * size);
            var items = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = reader.GetInt64(0),
                    started_at = reader.GetDateTime(1),
                    completed_at = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                    status = reader.GetString(3),
                    ledger_total = reader.GetDecimal(4),
                    hold_total = reader.GetDecimal(5),
                    mismatch_total = reader.GetDecimal(6),
                    checked_accounts = reader.GetInt64(7),
                    repaired_holds = reader.GetInt64(8),
                    repaired_projections = reader.GetInt64(9),
                    open_incidents = reader.GetInt64(10),
                    resolved_incidents = reader.GetInt64(11),
                    details = ParseJson(reader.GetString(12)),
                });
            }
            return Results.Ok(new { items, total, page, size });
        });

        group.MapGet("/incidents", async (
            NpgsqlDataSource dataSource,
            string? status,
            string? kind,
            long? userId,
            string? leaseToken,
            int page = 1,
            int size = 50,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 200);
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            var filters = new List<string>();
            AddFilter(command, filters, "status", status);
            AddFilter(command, filters, "kind", kind);
            if (userId.HasValue)
            {
                filters.Add($"user_id = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(userId.Value);
            }
            AddFilter(command, filters, "lease_token", leaseToken);
            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";

            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM accounting_reconciliation_incidents {where}";
            foreach (NpgsqlParameter parameter in command.Parameters)
                count.Parameters.AddWithValue(parameter.Value ?? DBNull.Value);
            var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));

            var limitParameter = command.Parameters.Count + 1;
            var offsetParameter = command.Parameters.Count + 2;
            command.CommandText = $"""
                SELECT id, incident_key, kind, severity, user_id, lease_token,
                       status, expected::text, actual::text, occurrences,
                       first_seen_at, last_seen_at, resolved_at, last_run_id
                FROM accounting_reconciliation_incidents
                {where}
                ORDER BY CASE WHEN status = 'open' THEN 0 ELSE 1 END,
                         last_seen_at DESC, id DESC
                LIMIT ${limitParameter} OFFSET ${offsetParameter}
                """;
            command.Parameters.AddWithValue(size);
            command.Parameters.AddWithValue((page - 1) * size);
            var items = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = reader.GetInt64(0),
                    incident_key = reader.GetString(1),
                    kind = reader.GetString(2),
                    severity = reader.GetString(3),
                    user_id = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
                    lease_token = reader.IsDBNull(5) ? null : reader.GetString(5),
                    status = reader.GetString(6),
                    expected = ParseJson(reader.GetString(7)),
                    actual = ParseJson(reader.GetString(8)),
                    occurrences = reader.GetInt64(9),
                    first_seen_at = reader.GetDateTime(10),
                    last_seen_at = reader.GetDateTime(11),
                    resolved_at = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12),
                    last_run_id = reader.IsDBNull(13) ? (long?)null : reader.GetInt64(13),
                });
            }
            return Results.Ok(new { items, total, page, size });
        });
    }

    private static void AddFilter(
        NpgsqlCommand command,
        List<string> filters,
        string column,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        filters.Add($"{column} = ${command.Parameters.Count + 1}");
        command.Parameters.AddWithValue(value.Trim());
    }

    private static JsonElement ParseJson(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);
}
