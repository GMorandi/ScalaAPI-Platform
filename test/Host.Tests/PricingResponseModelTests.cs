using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class PricingResponseModelTests
{
    [Fact]
    public async Task SearchQueryUnitComputesCostCorrectly()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-search-{suffix}";
        var requestId = $"request-search-{suffix}";
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:SearchPerQuery"] = "0.005",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96001, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, requestId, "hash", 96000, 96001, 96002, 96003,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 0, 0, 0, 0, 0, 0, 200, false, false,
                SearchQueryCount: 10));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, search_query_count FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            // 10 queries * $0.005/query = $0.05
            Assert.Equal(0.05m, reader.GetDecimal(0));
            Assert.Equal(10, reader.GetInt32(1));
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96001);
        }
    }

    [Fact]
    public async Task AudioMinuteUnitComputesCostCorrectly()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-audio-{suffix}";
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:AudioPerMinute"] = "0.06",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96101, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, $"request-audio-{suffix}", "hash", 96100, 96101, 96102, 96103,
                "gpt-4o", "gpt-4o", "audio", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 0, 0, 0, 0, 0, 0, 200, false, false,
                AudioMinutes: 5.5m));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, audio_minutes FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            // 5.5 minutes * $0.06/minute = $0.33
            Assert.Equal(0.33m, reader.GetDecimal(0));
            Assert.Equal(5.5m, reader.GetDecimal(1));
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96101);
        }
    }

    [Fact]
    public async Task CharacterCountUnitComputesCostCorrectly()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-char-{suffix}";
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:CharacterPerMillion"] = "2.0",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96201, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, $"request-char-{suffix}", "hash", 96200, 96201, 96202, 96203,
                "gpt-4o", "gpt-4o", "tts", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 0, 0, 0, 0, 0, 0, 200, false, false,
                CharacterCount: 500_000));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, character_count FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            // 500000 chars * $2.0 / 1000000 = $1.0
            Assert.Equal(1.0m, reader.GetDecimal(0));
            Assert.Equal(500_000, reader.GetInt32(1));
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96201);
        }
    }

    [Fact]
    public async Task LongContextTokenUnitComputesCostCorrectly()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-longctx-{suffix}";
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:LongContextPerMillion"] = "4.0",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96301, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, $"request-longctx-{suffix}", "hash", 96300, 96301, 96302, 96303,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 0, 0, 0, 0, 0, 0, 200, false, false,
                LongContextTokenCount: 250_000));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, long_context_token_count FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            // 250000 tokens * $4.0 / 1000000 = $1.0
            Assert.Equal(1.0m, reader.GetDecimal(0));
            Assert.Equal(250_000, reader.GetInt32(1));
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96301);
        }
    }

    [Fact]
    public async Task ModelMismatchObservedCheaperBillsAtObservedPrice()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-mismatch-cheap-{suffix}";
        // Request expensive model (claude-opus-4 at 15/75), but observed is cheap (gpt-4o-mini at 0.15/0.60)
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:claude-opus-4:InputPerMillion"] = "15",
                ["Pricing:Models:claude-opus-4:OutputPerMillion"] = "75",
                ["Pricing:Models:gpt-4o-mini:InputPerMillion"] = "0.15",
                ["Pricing:Models:gpt-4o-mini:OutputPerMillion"] = "0.60",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96401, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, $"request-mismatch-cheap-{suffix}", "hash", 96400, 96401, 96402, 96403,
                "claude-opus-4", "claude-opus-4", "chat_completions", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            // Observed model is gpt-4o-mini which is much cheaper
            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 1000, 100, 0, 0, 0, 0, 200, false, false,
                ObservedModel: "gpt-4o-mini"));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, model_mismatch_detected, model_mismatch_billing_model FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var cost = reader.GetDecimal(0);
            Assert.True(reader.GetBoolean(1)); // model_mismatch_detected
            Assert.Equal("gpt-4o-mini", reader.GetString(2)); // billing model
            // Should bill at gpt-4o-mini price: 1000*0.15/1M + 100*0.60/1M = 0.00015 + 0.00006 = 0.00021
            Assert.Equal(0.00021m, cost);
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96401);
        }
    }

    [Fact]
    public async Task ModelMismatchObservedMoreExpensiveBillsAtRequestedPrice()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-mismatch-expensive-{suffix}";
        // Request cheap model (gpt-4o-mini), but observed is expensive (claude-opus-4)
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:claude-opus-4:InputPerMillion"] = "15",
                ["Pricing:Models:claude-opus-4:OutputPerMillion"] = "75",
                ["Pricing:Models:gpt-4o-mini:InputPerMillion"] = "0.15",
                ["Pricing:Models:gpt-4o-mini:OutputPerMillion"] = "0.60",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96501, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, $"request-mismatch-exp-{suffix}", "hash", 96500, 96501, 96502, 96503,
                "gpt-4o-mini", "gpt-4o-mini", "chat_completions", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            // Observed model is claude-opus-4 which is much more expensive
            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 1000, 100, 0, 0, 0, 0, 200, false, false,
                ObservedModel: "claude-opus-4"));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, model_mismatch_detected, model_mismatch_billing_model FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var cost = reader.GetDecimal(0);
            Assert.True(reader.GetBoolean(1)); // model_mismatch_detected
            Assert.Equal("gpt-4o-mini", reader.GetString(2)); // billing at requested model
            // Should bill at gpt-4o-mini price (no auto-upgrade): 1000*0.15/1M + 100*0.60/1M = 0.00021
            Assert.Equal(0.00021m, cost);
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96501);
        }
    }

    [Fact]
    public async Task ModelMismatchObservedNoPriceBillsAtRequestedPrice()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-mismatch-noprice-{suffix}";
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "2.50",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "10",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96601, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, $"request-mismatch-np-{suffix}", "hash", 96600, 96601, 96602, 96603,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            // Observed model has no price configured
            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 1000, 100, 0, 0, 0, 0, 200, false, false,
                ObservedModel: "unknown-model-xyz"));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, model_mismatch_detected, model_mismatch_billing_model FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var cost = reader.GetDecimal(0);
            Assert.True(reader.GetBoolean(1)); // model_mismatch_detected
            Assert.Equal("gpt-4o", reader.GetString(2)); // billing at requested (never zero)
            // Should bill at gpt-4o price: 1000*2.50/1M + 100*10/1M = 0.0025 + 0.001 = 0.0035
            Assert.Equal(0.0035m, cost);
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96601);
        }
    }

    [Fact]
    public async Task ModelMatchObservedSameAsRequestedNormalBilling()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-match-{suffix}";
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "2.50",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "10",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96701, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, $"request-match-{suffix}", "hash", 96700, 96701, 96702, 96703,
                "gpt-4o", "gpt-4o", "chat_completions", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            // Observed model matches requested
            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 1000, 100, 0, 0, 0, 0, 200, false, false,
                ObservedModel: "gpt-4o"));
            Assert.True(completed.Accepted);

            await using var cmd = dataSource.CreateCommand(
                "SELECT cost_usd, model_mismatch_detected FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0.0035m, reader.GetDecimal(0));
            Assert.False(reader.GetBoolean(1)); // no mismatch
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96701);
        }
    }

    [Fact]
    public async Task MediaSettlementUsesRealPricingVersionNotHardcoded()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"lease-media-pv-{suffix}";
        var requestId = $"request-media-pv-{suffix}";
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:OutputPerMillion"] = "0",
                ["Pricing:Models:gpt-4o:ImageOutputPerUnit"] = "0.08",
            }).Build());
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            96801, $"test-funding:{suffix}", "test_credit", 100m));
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        try
        {
            Assert.True(await store.CreateAsync(new LeaseCreateRequest(
                leaseToken, requestId, "hash", 96800, 96801, 96802, 96803,
                "gpt-4o", "gpt-4o", "images", 1m, null, 0m,
                DateTime.UtcNow.AddMinutes(10))));

            // Verify the lease has a pricing version from the snapshot
            var lease = await store.GetByLeaseTokenAsync(leaseToken);
            Assert.NotNull(lease);
            Assert.Equal("runtime-v1", lease!.PricingVersion);

            // Simulate what MediaOperationHostedService does: load lease, use its pricing version
            var settlement = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 0, 0, 0, 0, 0, 0, 200, false, false,
                OutputImageCount: 2, ImageSize: "1024x1024",
                MediaOperationId: "op-test",
                PricingVersion: lease.PricingVersion ?? ""));
            Assert.True(settlement.Accepted);

            // Verify the usage event has the real pricing version, not hardcoded "v1"
            await using var cmd = dataSource.CreateCommand(
                "SELECT pricing_version, cost_usd FROM usage_events WHERE lease_token = $1");
            cmd.Parameters.AddWithValue(leaseToken);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("runtime-v1", reader.GetString(0)); // NOT "v1"
            Assert.Equal(0.16m, reader.GetDecimal(1)); // 2 * $0.08
        }
        finally
        {
            await CleanupAsync(dataSource, leaseToken, 96801);
        }
    }

    private static async Task CleanupAsync(NpgsqlDataSource dataSource, string leaseToken, long userId)
    {
        foreach (var table in new[] { "usage_outbox", "usage_logs", "usage_events", "balance_ledger" })
        {
            await using var cleanup = dataSource.CreateCommand(
                $"DELETE FROM {table} WHERE lease_token = $1");
            cleanup.Parameters.AddWithValue(leaseToken);
            await cleanup.ExecuteNonQueryAsync();
        }
        await using (var cleanupLease = dataSource.CreateCommand(
            "DELETE FROM request_leases WHERE lease_token = $1"))
        {
            cleanupLease.Parameters.AddWithValue(leaseToken);
            await cleanupLease.ExecuteNonQueryAsync();
        }
        foreach (var table in new[] { "accounting_projection_outbox", "balance_ledger", "accounting_accounts" })
        {
            await using var cleanup = dataSource.CreateCommand(
                $"DELETE FROM {table} WHERE user_id = $1");
            cleanup.Parameters.AddWithValue(userId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
