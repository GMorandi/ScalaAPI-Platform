using SqlSugar;

namespace ScalaAPI.Data.Entities;

[SugarTable("usage_logs")]
public class UsageLogEntity
{
    [SugarColumn(IsPrimaryKey = true, ColumnName = "request_id")]
    public string RequestId { get; set; } = "";

    [SugarColumn(ColumnName = "lease_token")]
    public string LeaseToken { get; set; } = "";

    [SugarColumn(ColumnName = "api_key_id")]
    public long ApiKeyId { get; set; }

    [SugarColumn(ColumnName = "user_id")]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "account_id")]
    public long AccountId { get; set; }

    [SugarColumn(ColumnName = "group_id")]
    public long GroupId { get; set; }

    [SugarColumn(ColumnName = "model")]
    public string Model { get; set; } = "";

    [SugarColumn(ColumnName = "upstream_model")]
    public string UpstreamModel { get; set; } = "";

    [SugarColumn(ColumnName = "input_tokens")]
    public int InputTokens { get; set; }

    [SugarColumn(ColumnName = "output_tokens")]
    public int OutputTokens { get; set; }

    [SugarColumn(ColumnName = "cache_create_tokens")]
    public int CacheCreateTokens { get; set; }

    [SugarColumn(ColumnName = "cache_read_tokens")]
    public int CacheReadTokens { get; set; }

    [SugarColumn(ColumnName = "cost_usd", DecimalDigits = 8)]
    public decimal CostUsd { get; set; }

    [SugarColumn(ColumnName = "duration_ms")]
    public int DurationMs { get; set; }

    [SugarColumn(ColumnName = "first_token_ms")]
    public int FirstTokenMs { get; set; }

    [SugarColumn(ColumnName = "stream")]
    public bool Stream { get; set; }

    [SugarColumn(ColumnName = "client_disconnect")]
    public bool ClientDisconnect { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
