using ScalaAPI.Data.Repositories;
using Npgsql;

namespace ScalaAPI.Admin.Endpoints;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/usage")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IUsageLogRepository repo,
            long? userId, string? model, DateTime? from, DateTime? to,
            int page = 1, int size = 20) =>
        {
            if (page < 1) page = 1;
            if (size < 1 || size > 100) size = 20;

            var items = await repo.GetPaged(userId, model, from, to, page, size);
            var total = await repo.Count(userId, model, from, to);

            return Results.Ok(new
            {
                items,
                total,
                page,
                size,
                pages = (int)Math.Ceiling((double)total / size)
            });
        });

        group.MapGet("/ledger", async (NpgsqlDataSource dataSource,
            long? userId, string? leaseToken, int page = 1, int size = 50) =>
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 200);
            var filters = new List<string>();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            if (userId.HasValue)
            {
                filters.Add($"user_id = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(userId.Value);
            }
            if (!string.IsNullOrWhiteSpace(leaseToken))
            {
                filters.Add($"lease_token = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(leaseToken);
            }

            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM balance_ledger {where}";
            foreach (NpgsqlParameter parameter in command.Parameters)
                count.Parameters.AddWithValue(parameter.Value ?? DBNull.Value);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync());

            var limitParameter = command.Parameters.Count + 1;
            var offsetParameter = command.Parameters.Count + 2;
            command.CommandText = $"""
                SELECT id, user_id, payment_id, reference, amount, created_at,
                       lease_token, entry_type
                FROM balance_ledger {where}
                ORDER BY id DESC
                LIMIT ${limitParameter} OFFSET ${offsetParameter}
                """;
            command.Parameters.AddWithValue(size);
            command.Parameters.AddWithValue((page - 1) * size);
            var items = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new
                {
                    id = reader.GetInt64(0),
                    user_id = reader.GetInt64(1),
                    payment_id = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                    reference = reader.IsDBNull(3) ? null : reader.GetString(3),
                    amount = reader.GetDecimal(4),
                    created_at = reader.GetDateTime(5),
                    lease_token = reader.IsDBNull(6) ? null : reader.GetString(6),
                    entry_type = reader.GetString(7),
                });
            }
            return Results.Ok(new { items, total, page, size });
        });

        group.MapGet("/leases", async (NpgsqlDataSource dataSource,
            long? userId, string? requestId, string? status, int page = 1, int size = 50) =>
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 200);
            var filters = new List<string>();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            if (userId.HasValue)
            {
                filters.Add($"user_id = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(userId.Value);
            }
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                filters.Add($"request_id = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(requestId);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                filters.Add($"status = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(status);
            }
            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM request_leases {where}";
            foreach (NpgsqlParameter parameter in command.Parameters)
                count.Parameters.AddWithValue(parameter.Value ?? DBNull.Value);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync());

            var limitParameter = command.Parameters.Count + 1;
            var offsetParameter = command.Parameters.Count + 2;
            command.CommandText = $"""
                SELECT lease_token, request_id, api_key_id, user_id, account_id,
                       group_id, model, upstream_model, status, hold_amount,
                       final_cost_usd, abort_reason, created_at, expires_at, finalized_at
                FROM request_leases {where}
                ORDER BY created_at DESC
                LIMIT ${limitParameter} OFFSET ${offsetParameter}
                """;
            command.Parameters.AddWithValue(size);
            command.Parameters.AddWithValue((page - 1) * size);
            var items = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new
                {
                    lease_token = reader.GetString(0),
                    request_id = reader.GetString(1),
                    api_key_id = reader.GetInt64(2),
                    user_id = reader.GetInt64(3),
                    account_id = reader.GetInt64(4),
                    group_id = reader.GetInt64(5),
                    model = reader.GetString(6),
                    upstream_model = reader.GetString(7),
                    status = reader.GetString(8),
                    hold_amount = reader.GetDecimal(9),
                    final_cost_usd = reader.IsDBNull(10) ? (decimal?)null : reader.GetDecimal(10),
                    abort_reason = reader.IsDBNull(11) ? null : reader.GetString(11),
                    created_at = reader.GetDateTime(12),
                    expires_at = reader.GetDateTime(13),
                    finalized_at = reader.IsDBNull(14) ? (DateTime?)null : reader.GetDateTime(14),
                });
            }
            return Results.Ok(new { items, total, page, size });
        });

        group.MapGet("/holds", async (NpgsqlDataSource dataSource,
            long? userId, string? leaseToken, string? status, int page = 1, int size = 50) =>
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 200);
            var filters = new List<string>();
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            if (userId.HasValue)
            {
                filters.Add($"user_id = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(userId.Value);
            }
            if (!string.IsNullOrWhiteSpace(leaseToken))
            {
                filters.Add($"lease_token = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(leaseToken);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                filters.Add($"status = ${command.Parameters.Count + 1}");
                command.Parameters.AddWithValue(status);
            }
            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM balance_holds {where}";
            foreach (NpgsqlParameter parameter in command.Parameters)
                count.Parameters.AddWithValue(parameter.Value ?? DBNull.Value);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync());

            var limitParameter = command.Parameters.Count + 1;
            var offsetParameter = command.Parameters.Count + 2;
            command.CommandText = $"""
                SELECT hold_id, user_id, lease_token, amount, status, created_at, finalized_at
                FROM balance_holds {where}
                ORDER BY created_at DESC
                LIMIT ${limitParameter} OFFSET ${offsetParameter}
                """;
            command.Parameters.AddWithValue(size);
            command.Parameters.AddWithValue((page - 1) * size);
            var items = new List<object>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new
                {
                    hold_id = reader.GetString(0),
                    user_id = reader.GetInt64(1),
                    lease_token = reader.IsDBNull(2) ? null : reader.GetString(2),
                    amount = reader.GetDecimal(3),
                    status = reader.GetString(4),
                    created_at = reader.GetDateTime(5),
                    finalized_at = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                });
            }
            return Results.Ok(new { items, total, page, size });
        });

        group.MapPost("/reconcile", async (NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            decimal usageTotal;
            decimal ledgerTotal;
            decimal holdTotal;
            long missingDebitCount;
            long activeHoldCount;

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT
                        COALESCE((SELECT sum(cost_usd) FROM usage_events), 0),
                        COALESCE((SELECT sum(-amount) FROM balance_ledger
                                  WHERE entry_type = 'usage_debit'), 0),
                        COALESCE((SELECT sum(amount) FROM balance_holds
                                  WHERE status = 'active'), 0),
                        (SELECT count(*) FROM usage_events e
                         WHERE e.cost_usd > 0
                           AND NOT EXISTS (
                               SELECT 1 FROM balance_ledger l
                               WHERE l.lease_token = e.lease_token
                                 AND l.entry_type = 'usage_debit')),
                        (SELECT count(*) FROM balance_holds WHERE status = 'active')
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return Results.Problem("Unable to read reconciliation totals");
                usageTotal = reader.GetDecimal(0);
                ledgerTotal = reader.GetDecimal(1);
                holdTotal = reader.GetDecimal(2);
                missingDebitCount = reader.GetInt64(3);
                activeHoldCount = reader.GetInt64(4);
            }

            var mismatch = decimal.Round(usageTotal - ledgerTotal, 8, MidpointRounding.AwayFromZero);
            var status = mismatch == 0m && missingDebitCount == 0 ? "passed" : "failed";
            var details = System.Text.Json.JsonSerializer.Serialize(new
            {
                missing_debit_count = missingDebitCount,
                active_hold_count = activeHoldCount,
                checked_at = DateTimeOffset.UtcNow,
            });
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO ledger_reconciliation_runs(
                        completed_at, status, ledger_total, hold_total, mismatch_total, details)
                    VALUES (now(), $1, $2, $3, $4, CAST($5 AS jsonb))
                    RETURNING id, completed_at
                    """;
                insert.Parameters.AddWithValue(status);
                insert.Parameters.AddWithValue(ledgerTotal);
                insert.Parameters.AddWithValue(holdTotal);
                insert.Parameters.AddWithValue(mismatch);
                insert.Parameters.AddWithValue(details);
                await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                var runId = reader.GetInt64(0);
                var completedAt = reader.GetDateTime(1);
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return Results.Ok(new
                {
                    id = runId,
                    status,
                    usage_total = usageTotal,
                    ledger_total = ledgerTotal,
                    hold_total = holdTotal,
                    mismatch_total = mismatch,
                    missing_debit_count = missingDebitCount,
                    active_hold_count = activeHoldCount,
                    completed_at = completedAt,
                });
            }
        });
    }
}
