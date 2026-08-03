using Npgsql;
using Sub2Api.Data.Migration;

namespace Sub2Api.Admin.Endpoints;

public sealed record FenceTransitionRequest(
    string CurrentPrimary,
    string NextPrimary,
    string NextMode,
    string Reason,
    string UpdatedBy);

public static class MigrationEndpoints
{
    public static void MapMigrationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/migration").RequireAuthorization("AdminOnly");

        group.MapGet("/fence", async (MigrationFenceStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAsync(ct)));

        group.MapGet("/fence/history", async (int? limit, MigrationFenceStore store,
            CancellationToken ct) =>
            Results.Ok(await store.GetHistoryAsync(limit ?? 100, ct)));

        group.MapPost("/fence/promote", async (FenceTransitionRequest request,
            MigrationFenceStore store, CancellationToken ct) =>
        {
            try
            {
                var result = await store.PromoteAsync(request.CurrentPrimary, request.NextPrimary,
                    request.NextMode, request.Reason, request.UpdatedBy, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/cdc/dead-letters/{eventId}/replay", async (string eventId,
            CdcInboxStore store, CancellationToken ct) =>
            await store.ReplayDeadLetterAsync(eventId, ct)
                ? Results.Ok(new { eventId, status = "queued" })
                : Results.NotFound(new { eventId, error = "dead_letter_not_found" }));

        group.MapGet("/health", async (NpgsqlDataSource db, MigrationFenceStore fence,
            MigrationWriteGate writeGate,
            CancellationToken ct) =>
        {
            await using var command = db.CreateCommand("""
                SELECT
                  (SELECT count(*) FROM cdc_inbox WHERE status IN ('pending', 'failed')),
                  (SELECT count(*) FROM cdc_inbox WHERE status = 'dead_letter'),
                  (SELECT min(received_at) FROM cdc_inbox WHERE status IN ('pending', 'failed')),
                  (SELECT max(updated_at) FROM cdc_checkpoints),
                  (SELECT count(*) FROM cdc_rejected_messages)
                """);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return Results.Ok(new
            {
                fence = await fence.GetAsync(ct),
                pending = reader.GetInt64(0),
                deadLetters = reader.GetInt64(1),
                oldestPendingAt = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2),
                checkpointUpdatedAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3),
                rejectedMessages = reader.GetInt64(4),
                fenceRejections = writeGate.RejectionCount,
                lagSeconds = reader.IsDBNull(2) ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - reader.GetFieldValue<DateTimeOffset>(2)).TotalSeconds)
            });
        });
    }
}
