using Npgsql;

namespace Sub2Api.Data.Migration;

/// <summary>
/// Durable bookkeeping for the restricted credential topic. The plaintext
/// payload is intentionally never accepted by this store.
/// </summary>
public sealed class CdcCredentialStore(NpgsqlDataSource dataSource)
{
    public async Task<bool> EnqueueAsync(CredentialEnvelope envelope, CancellationToken ct = default)
    {
        envelope.Validate();
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext["enc:v1:".Length..]);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO cdc_credential_payloads(
                event_id, epoch, aggregate_type, aggregate_id, key_version,
                ciphertext, payload_hash, source_lsn, transaction_id, operation, occurred_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            ON CONFLICT (event_id) DO NOTHING
            """);
        command.Parameters.AddWithValue(envelope.EventId);
        command.Parameters.AddWithValue(envelope.Epoch);
        command.Parameters.AddWithValue(envelope.AggregateType);
        command.Parameters.AddWithValue(envelope.AggregateId);
        command.Parameters.AddWithValue(envelope.KeyVersion);
        command.Parameters.AddWithValue(ciphertext);
        command.Parameters.AddWithValue(envelope.PayloadHash);
        command.Parameters.AddWithValue(envelope.SourceLsn);
        command.Parameters.AddWithValue(envelope.TransactionId);
        command.Parameters.AddWithValue(envelope.Operation);
        command.Parameters.AddWithValue(envelope.OccurredAt);
        if (await command.ExecuteNonQueryAsync(ct) == 1) return true;

        await using var existing = dataSource.CreateCommand(
            "SELECT payload_hash FROM cdc_credential_payloads WHERE event_id = $1");
        existing.Parameters.AddWithValue(envelope.EventId);
        var existingHash = (string?)await existing.ExecuteScalarAsync(ct);
        if (!string.Equals(existingHash, envelope.PayloadHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"credential event_id {envelope.EventId} already exists with a different payload_hash");
        return false;
    }

    public async Task MarkAppliedAsync(string eventId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE cdc_credential_payloads SET applied_at = now() WHERE event_id = $1");
        command.Parameters.AddWithValue(eventId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
