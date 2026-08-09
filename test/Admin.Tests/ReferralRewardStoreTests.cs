using Npgsql;
using ScalaAPI.Admin.Data;
using ScalaAPI.Data.Accounting;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class ReferralRewardStoreTests
{
    [Fact]
    public async Task RewardIsAtomicIdempotentAuditedAndSingleAttribution()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var referrerId = 9_000_000L + Random.Shared.Next(1, 400_000);
        var referredId = referrerId + 1;
        var actorId = referrerId + 2;
        var key = $"referral-{Guid.NewGuid():N}";
        var referrerRegistryKey = $"referral-referrer-{Guid.NewGuid():N}";
        var referredRegistryKey = $"referral-referred-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);

        await using (var setup = dataSource.CreateCommand("""
            INSERT INTO entity_registry(entity_type, entity_key, entity_id)
            VALUES ('user', $1, $2), ('user', $3, $4)
            """))
        {
            setup.Parameters.AddWithValue(referrerRegistryKey);
            setup.Parameters.AddWithValue(referrerId);
            setup.Parameters.AddWithValue(referredRegistryKey);
            setup.Parameters.AddWithValue(referredId);
            await setup.ExecuteNonQueryAsync();
        }
        await using (var code = dataSource.CreateCommand(
            "INSERT INTO referral_codes(user_id, code) VALUES ($1, $2)"))
        {
            code.Parameters.AddWithValue(referrerId);
            code.Parameters.AddWithValue($"code-{Guid.NewGuid():N}");
            await code.ExecuteNonQueryAsync();
        }

        try
        {
            var store = new ReferralRewardStore(dataSource, new AccountingStore(dataSource));
            var created = await store.RecordAsync(
                actorId, referrerId, referredId, 3.25m, key,
                "Verified referral reward", "127.0.0.1");
            var replay = await store.RecordAsync(
                actorId, referrerId, referredId, 3.25m, key,
                "Verified referral reward", "127.0.0.1");
            var conflict = await store.RecordAsync(
                actorId, referrerId, referredId, 4.25m, $"conflict-{Guid.NewGuid():N}",
                "Changed reward", "127.0.0.1");

            Assert.Equal(ReferralRewardStatus.Created, created.Status);
            Assert.Equal(ReferralRewardStatus.Replay, replay.Status);
            Assert.Equal(ReferralRewardStatus.Conflict, conflict.Status);
            Assert.Equal(created.RecordId, replay.RecordId);
            Assert.Equal(created.LedgerId, replay.LedgerId);
            Assert.Equal(1, created.Snapshot.Version);
            Assert.Equal(3.25m, replay.Snapshot.Balance);

            await using var verify = dataSource.CreateCommand("""
                SELECT
                    (SELECT count(*) FROM referral_records
                     WHERE referrer_user_id = $1 AND referred_user_id = $2),
                    (SELECT bonus_usd FROM referral_records
                     WHERE referrer_user_id = $1 AND referred_user_id = $2),
                    (SELECT total_referrals FROM referral_codes WHERE user_id = $1),
                    (SELECT total_bonus_usd FROM referral_codes WHERE user_id = $1),
                    (SELECT count(*) FROM balance_ledger
                     WHERE user_id = $1 AND entry_type = 'referral_bonus'),
                    (SELECT count(*) FROM audit_logs
                     WHERE user_id = $3 AND action = 'referral.reward');
                """);
            verify.Parameters.AddWithValue(referrerId);
            verify.Parameters.AddWithValue(referredId);
            verify.Parameters.AddWithValue(actorId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(3.25m, reader.GetDecimal(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(3.25m, reader.GetDecimal(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.Equal(1L, reader.GetInt64(5));
        }
        finally
        {
            foreach (var statement in new[]
            {
                "DELETE FROM referral_records WHERE referrer_user_id = $1 OR referred_user_id = $2",
                "DELETE FROM referral_codes WHERE user_id = $1",
                "DELETE FROM audit_logs WHERE user_id = $3 AND action = 'referral.reward'",
                "DELETE FROM accounting_projection_outbox WHERE user_id = $1",
                "DELETE FROM balance_ledger WHERE user_id = $1",
                "DELETE FROM accounting_accounts WHERE user_id = $1",
                "DELETE FROM entity_registry WHERE entity_type = 'user' AND (entity_id = $1 OR entity_id = $2)",
            })
            {
                await using var cleanup = dataSource.CreateCommand(statement);
                cleanup.Parameters.AddWithValue(referrerId);
                cleanup.Parameters.AddWithValue(referredId);
                cleanup.Parameters.AddWithValue(actorId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
