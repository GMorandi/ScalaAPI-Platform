using SqlSugar;

namespace Sub2Api.Data.Entities;

[SugarTable("user_accounts")]
public class UserAccountEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "email", IsNullable = false)]
    public string Email { get; set; } = "";

    [SugarColumn(ColumnName = "password_hash", IsNullable = true)]
    public string? PasswordHash { get; set; }

    [SugarColumn(ColumnName = "display_name", IsNullable = true)]
    public string? DisplayName { get; set; }

    [SugarColumn(ColumnName = "status")]
    public string Status { get; set; } = "active";

    [SugarColumn(ColumnName = "role")]
    public string Role { get; set; } = "user";

    [SugarColumn(ColumnName = "oauth_provider", IsNullable = true)]
    public string? OAuthProvider { get; set; }

    [SugarColumn(ColumnName = "oauth_id", IsNullable = true)]
    public string? OAuthId { get; set; }

    [SugarColumn(ColumnName = "totp_secret", IsNullable = true)]
    public string? TotpSecret { get; set; }

    [SugarColumn(ColumnName = "totp_enabled")]
    public bool TotpEnabled { get; set; }

    [SugarColumn(ColumnName = "totp_backup_codes", IsNullable = true, ColumnDataType = "text")]
    public string? TotpBackupCodes { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [SugarColumn(ColumnName = "last_login_at", IsNullable = true)]
    public DateTime? LastLoginAt { get; set; }
}

[SugarTable("announcements")]
public class AnnouncementEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "title")]
    public string Title { get; set; } = "";

    [SugarColumn(ColumnName = "content", ColumnDataType = "text")]
    public string Content { get; set; } = "";

    [SugarColumn(ColumnName = "status")]
    public string Status { get; set; } = "published";

    [SugarColumn(ColumnName = "priority")]
    public int Priority { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [SugarColumn(ColumnName = "expires_at", IsNullable = true)]
    public DateTime? ExpiresAt { get; set; }
}

[SugarTable("audit_logs")]
public class AuditLogEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "user_id")]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "action")]
    public string Action { get; set; } = "";

    [SugarColumn(ColumnName = "resource_type", IsNullable = true)]
    public string? ResourceType { get; set; }

    [SugarColumn(ColumnName = "resource_id", IsNullable = true)]
    public string? ResourceId { get; set; }

    [SugarColumn(ColumnName = "details", IsNullable = true, ColumnDataType = "text")]
    public string? Details { get; set; }

    [SugarColumn(ColumnName = "ip_address", IsNullable = true)]
    public string? IpAddress { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[SugarTable("ops_metrics")]
public class OpsMetricsEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "metric_name")]
    public string MetricName { get; set; } = "";

    [SugarColumn(ColumnName = "metric_value", DecimalDigits = 4)]
    public decimal MetricValue { get; set; }

    [SugarColumn(ColumnName = "labels", IsNullable = true, ColumnDataType = "text")]
    public string? Labels { get; set; }

    [SugarColumn(ColumnName = "collected_at")]
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
}

[SugarTable("redeem_codes")]
public class RedeemCodeEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "code")]
    public string Code { get; set; } = "";

    [SugarColumn(ColumnName = "discount_amount", DecimalDigits = 2)]
    public decimal DiscountAmount { get; set; }

    [SugarColumn(ColumnName = "bonus_amount", DecimalDigits = 2)]
    public decimal BonusAmount { get; set; }

    [SugarColumn(ColumnName = "max_uses")]
    public int MaxUses { get; set; }

    [SugarColumn(ColumnName = "used_count")]
    public int UsedCount { get; set; }

    [SugarColumn(ColumnName = "status")]
    public string Status { get; set; } = "active";

    [SugarColumn(ColumnName = "expires_at", IsNullable = true)]
    public DateTime? ExpiresAt { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[SugarTable("payment_orders")]
public class PaymentOrderEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "user_id")]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "amount", DecimalDigits = 2)]
    public decimal Amount { get; set; }

    [SugarColumn(ColumnName = "currency")]
    public string Currency { get; set; } = "USD";

    [SugarColumn(ColumnName = "provider")]
    public string Provider { get; set; } = "";

    [SugarColumn(ColumnName = "provider_order_id", IsNullable = true)]
    public string? ProviderOrderId { get; set; }

    [SugarColumn(ColumnName = "status")]
    public string Status { get; set; } = "pending";

    [SugarColumn(ColumnName = "description", IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [SugarColumn(ColumnName = "paid_at", IsNullable = true)]
    public DateTime? PaidAt { get; set; }
}

[SugarTable("subscription_plans")]
public class SubscriptionPlanEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "name")]
    public string Name { get; set; } = "";

    [SugarColumn(ColumnName = "price_monthly", DecimalDigits = 2)]
    public decimal PriceMonthly { get; set; }

    [SugarColumn(ColumnName = "quota_usd", DecimalDigits = 2)]
    public decimal QuotaUsd { get; set; }

    [SugarColumn(ColumnName = "features", IsNullable = true, ColumnDataType = "text")]
    public string? Features { get; set; }

    [SugarColumn(ColumnName = "status")]
    public string Status { get; set; } = "active";
}

[SugarTable("user_subscriptions")]
public class UserSubscriptionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "user_id")]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "plan_id")]
    public long PlanId { get; set; }

    [SugarColumn(ColumnName = "status")]
    public string Status { get; set; } = "active";

    [SugarColumn(ColumnName = "started_at")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    [SugarColumn(ColumnName = "expires_at", IsNullable = true)]
    public DateTime? ExpiresAt { get; set; }
}

[SugarTable("channel_monitors")]
public class ChannelMonitorEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnName = "id")]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "account_id")]
    public long AccountId { get; set; }

    [SugarColumn(ColumnName = "status")]
    public string Status { get; set; } = "unknown";

    [SugarColumn(ColumnName = "latency_ms")]
    public int LatencyMs { get; set; }

    [SugarColumn(ColumnName = "last_error", IsNullable = true)]
    public string? LastError { get; set; }

    [SugarColumn(ColumnName = "checked_at")]
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

[SugarTable("usage_summary_daily")]
public class UsageSummaryDailyEntity
{
    [SugarColumn(IsPrimaryKey = true, ColumnName = "id")]
    public string Id { get; set; } = "";

    [SugarColumn(ColumnName = "user_id")]
    public long UserId { get; set; }

    [SugarColumn(ColumnName = "model")]
    public string Model { get; set; } = "";

    [SugarColumn(ColumnName = "date")]
    public DateTime Date { get; set; }

    [SugarColumn(ColumnName = "request_count")]
    public int RequestCount { get; set; }

    [SugarColumn(ColumnName = "input_tokens")]
    public long InputTokens { get; set; }

    [SugarColumn(ColumnName = "output_tokens")]
    public long OutputTokens { get; set; }

    [SugarColumn(ColumnName = "total_cost_usd", DecimalDigits = 6)]
    public decimal TotalCostUsd { get; set; }
}
