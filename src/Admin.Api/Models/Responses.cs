namespace Sub2Api.Admin.Models;

public record PagedResponse<T>(List<T> Items, int Total, int Page, int Size);

public record DashboardStats(
    int TotalAccounts, int TotalGroups, int TotalUsers, int TotalApiKeys);

public record ApiKeyCreateResponse(string Key, long Id);
