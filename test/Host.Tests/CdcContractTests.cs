using System.Text.Json;
using Sub2Api.Data.Migration;
using Xunit;

namespace Sub2Api.Host.Tests;

public class CdcContractTests
{
    [Fact]
    public void DebeziumCreateIsNormalizedWithDeterministicIdentity()
    {
        using var doc = JsonDocument.Parse("""
            {"op":"c","source":{"table":"users","lsn":42,"txId":7,"ts_ms":1700000000000},"after":{"id":12,"role":"user","balance":"1.25"}}
            """);

        var first = DebeziumEnvelopeAdapter.Adapt(doc.RootElement, 1);
        var second = DebeziumEnvelopeAdapter.Adapt(doc.RootElement, 1);

        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal("user", first.AggregateType);
        Assert.Equal("12", first.AggregateId);
        Assert.Equal("insert", first.Operation);
        Assert.Equal(1, first.SchemaVersion);
        first.Validate();
    }

    [Fact]
    public void DebeziumDecimalStringSurvivesNormalization()
    {
        using var doc = JsonDocument.Parse("""
            {"op":"u","source":{"table":"users","lsn":43,"txId":8,"ts_ms":1700000000000},"after":{"id":12,"balance":"12.34000000","concurrency":"5"}}
            """);

        var envelope = DebeziumEnvelopeAdapter.Adapt(doc.RootElement, 1);

        Assert.Equal("12.34000000", envelope.Payload.GetProperty("balance").GetString());
        Assert.Equal("5", envelope.Payload.GetProperty("concurrency").GetString());
        envelope.Validate();
    }

    [Fact]
    public void DebeziumSnapshotMarkerIsPreservedForReadinessGating()
    {
        using var doc = JsonDocument.Parse("""
            {"op":"r","source":{"table":"users","lsn":"0/20","txId":8,"snapshot":"last","ts_ms":1700000000000},"after":{"id":12}}
            """);

        var envelope = DebeziumEnvelopeAdapter.Adapt(doc.RootElement, 1);

        Assert.Equal("snapshot", envelope.Operation);
        Assert.Equal("last", envelope.Snapshot);
        envelope.Validate();
    }

    [Fact]
    public void DebeziumThreePointNineNumericLsnAndJsonbOutboxPayloadAreNormalized()
    {
        using var doc = JsonDocument.Parse("""
            {"op":"r","source":{"table":"migration_cdc_outbox","lsn":27051288,"txId":746,"snapshot":"last","ts_ms":1700000000000},"after":{"event_id":"11111111-1111-4111-8111-111111111111","aggregate_type":"api_key","aggregate_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","operation":"snapshot","payload":"{\"api_key_id\":30,\"user_id\":1,\"group_id\":10}"}}
            """);

        var envelope = DebeziumEnvelopeAdapter.Adapt(doc.RootElement, 1);

        Assert.Equal("27051288", envelope.SourceLsn);
        Assert.Equal("last", envelope.Snapshot);
        Assert.Equal(30, envelope.Payload.GetProperty("api_key_id").GetInt64());
        envelope.Validate();
    }

    [Fact]
    public void DebeziumHeartbeatIsRecognizedAsNonBusinessRecord()
    {
        using var doc = JsonDocument.Parse("""{"ts_ms":1700000000000}""");
        Assert.False(doc.RootElement.TryGetProperty("op", out _));
        Assert.True(doc.RootElement.TryGetProperty("ts_ms", out _));
    }

    [Theory]
    [InlineData("0/0", 0)]
    [InlineData("1/10", 4294967312)]
    [InlineData("0/16B6C50", 23817296)]
    public void PostgresLsnParserProducesMonotonicNumericPosition(string text, decimal expected)
    {
        Assert.True(ChangeEnvelope.TryParseLsn(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void PayloadHashDetectsMutation()
    {
        using var doc = JsonDocument.Parse("""{"id":1}""");
        var hash = ChangeEnvelope.ComputePayloadHash(doc.RootElement);
        var envelope = new ChangeEnvelope(Guid.NewGuid().ToString(), 1, "0/10", "7", "user", "1",
            "update", 1, DateTimeOffset.UtcNow, hash, doc.RootElement.Clone());

        envelope.Validate();
        using var changed = JsonDocument.Parse("""{"id":2}""");
        Assert.NotEqual(hash, ChangeEnvelope.ComputePayloadHash(changed.RootElement));
    }

    [Fact]
    public void SemanticOutboxUsesHashedApiKeyIdentity()
    {
        const string eventId = "11111111-1111-4111-8111-111111111111";
        using var doc = JsonDocument.Parse("""
            {"op":"c","source":{"table":"migration_cdc_outbox","lsn":43,"txId":8,"ts_ms":1700000000001},"after":{"event_id":"11111111-1111-4111-8111-111111111111","aggregate_type":"api_key","aggregate_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","operation":"snapshot","payload":{"api_key_id":12,"user_id":7,"group_id":3}}}
            """);

        var envelope = DebeziumEnvelopeAdapter.Adapt(doc.RootElement, 1);

        Assert.Equal(eventId, envelope.EventId);
        Assert.Equal("api_key", envelope.AggregateType);
        Assert.Equal(64, envelope.AggregateId.Length);
        Assert.Equal("snapshot", envelope.Operation);
        envelope.Validate();
    }

    [Theory]
    [InlineData("password_hash")]
    [InlineData("totp_secret_encrypted")]
    [InlineData("credentials")]
    [InlineData("access_token")]
    [InlineData("key")]
    public void OrdinaryCdcRejectsRestrictedFields(string field)
    {
        using var doc = JsonDocument.Parse($$"""{"id":1,"{{field}}":"must-not-cross-cdc"}""");
        var envelope = new ChangeEnvelope(Guid.NewGuid().ToString(), 1, "0/10", "7", "user", "1",
            "update", 1, DateTimeOffset.UtcNow, ChangeEnvelope.ComputePayloadHash(doc.RootElement),
            doc.RootElement.Clone());

        var error = Assert.Throws<FormatException>(envelope.Validate);
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void OrdinaryAccountCdcRejectsPlatformExtraData()
    {
        using var doc = JsonDocument.Parse("""{"id":1,"extra":{"organization":"private"}}""");
        var envelope = new ChangeEnvelope(Guid.NewGuid().ToString(), 1, "0/10", "7", "account", "1",
            "update", 1, DateTimeOffset.UtcNow, ChangeEnvelope.ComputePayloadHash(doc.RootElement),
            doc.RootElement.Clone());

        Assert.Throws<FormatException>(envelope.Validate);
    }

    [Fact]
    public void CompositeUserAllowedGroupIdentityIsDeterministic()
    {
        using var doc = JsonDocument.Parse("""
            {"op":"c","source":{"table":"user_allowed_groups","lsn":44,"txId":9,"ts_ms":1700000000002},"after":{"user_id":12,"group_id":3}}
            """);

        var envelope = DebeziumEnvelopeAdapter.Adapt(doc.RootElement, 1);

        Assert.Equal("user_allowed_groups", envelope.AggregateType);
        Assert.Equal("12:3", envelope.AggregateId);
        envelope.Validate();
    }

    [Fact]
    public void CredentialEnvelopeRequiresSeparateEncryptedPayload()
    {
        var envelope = new CredentialEnvelope(
            "11111111-1111-4111-8111-111111111111", 1, "0/20", "9", "account", "12",
            "update", 1, "target-key-v1", "enc:v1:Y2lwaGVydGV4dA==",
            new string('a', 64), DateTimeOffset.UtcNow);

        envelope.Validate();

        var plain = envelope with { Ciphertext = "credentials-in-plain-text" };
        Assert.Throws<FormatException>(plain.Validate);
    }

    [Fact]
    public void FenceTransitionRejectsCanaryBypassAndInvalidModePrimaryPairs()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MigrationFenceStore.ValidateTransition("sub2api", "legacy_primary", "platform", "target_primary"));
        Assert.Throws<ArgumentException>(() =>
            MigrationFenceStore.ValidateTransition("sub2api", "legacy_primary", "platform", "legacy_read_only"));
        Assert.Throws<InvalidOperationException>(() =>
            MigrationFenceStore.ValidateTransition("platform", "target_primary", "platform", "target_primary"));
        Assert.Throws<InvalidOperationException>(() =>
            MigrationFenceStore.ValidateTransition("sub2api", "target_primary", "sub2api", "legacy_read_only"));

        MigrationFenceStore.ValidateTransition("sub2api", "legacy_primary", "platform", "target_canary");
        MigrationFenceStore.ValidateTransition("platform", "target_canary", "platform", "target_primary");
        MigrationFenceStore.ValidateTransition("platform", "target_primary", "sub2api", "legacy_read_only");
    }

    [Fact]
    public void FenceTransitionMatrixMatchesTheDocumentedFiniteStateMachine()
    {
        var states = new[]
        {
            (Primary: "sub2api", Mode: "legacy_primary"),
            (Primary: "platform", Mode: "target_canary"),
            (Primary: "platform", Mode: "target_primary"),
            (Primary: "sub2api", Mode: "legacy_read_only")
        };
        var legal = new HashSet<((string Primary, string Mode) From, (string Primary, string Mode) To)>
        {
            (("sub2api", "legacy_primary"), ("platform", "target_canary")),
            (("platform", "target_canary"), ("platform", "target_primary")),
            (("platform", "target_canary"), ("sub2api", "legacy_primary")),
            (("platform", "target_primary"), ("sub2api", "legacy_read_only")),
            (("sub2api", "legacy_read_only"), ("platform", "target_primary")),
            (("sub2api", "legacy_read_only"), ("sub2api", "legacy_primary"))
        };

        foreach (var from in states)
        foreach (var to in states)
        {
            var transition = (from, to);
            if (legal.Contains(transition))
            {
                MigrationFenceStore.ValidateTransition(
                    from.Primary, from.Mode, to.Primary, to.Mode);
            }
            else
            {
                Assert.ThrowsAny<Exception>(() => MigrationFenceStore.ValidateTransition(
                    from.Primary, from.Mode, to.Primary, to.Mode));
            }
        }
    }
}
