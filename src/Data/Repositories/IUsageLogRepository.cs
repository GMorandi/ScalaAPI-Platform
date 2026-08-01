using Sub2Api.Data.Entities;

namespace Sub2Api.Data.Repositories;

public interface IUsageLogRepository
{
    Task<List<UsageLogEntity>> GetPaged(long? userId, string? model,
        DateTime? from, DateTime? to, int page, int size);
    Task<int> Count(long? userId, string? model, DateTime? from, DateTime? to);
}
