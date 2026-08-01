using SqlSugar;
using Sub2Api.Data.Entities;

namespace Sub2Api.Data.Repositories;

public class UsageLogRepository : IUsageLogRepository
{
    private readonly ISqlSugarClient _db;

    public UsageLogRepository(ISqlSugarClient db) => _db = db;

    public async Task<List<UsageLogEntity>> GetPaged(long? userId, string? model,
        DateTime? from, DateTime? to, int page, int size)
    {
        var query = BuildQuery(userId, model, from, to);
        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();
    }

    public async Task<int> Count(long? userId, string? model, DateTime? from, DateTime? to)
    {
        var query = BuildQuery(userId, model, from, to);
        return await query.CountAsync();
    }

    private ISugarQueryable<UsageLogEntity> BuildQuery(long? userId, string? model,
        DateTime? from, DateTime? to)
    {
        var query = _db.Queryable<UsageLogEntity>();
        if (userId.HasValue)
            query = query.Where(x => x.UserId == userId.Value);
        if (!string.IsNullOrEmpty(model))
            query = query.Where(x => x.Model == model);
        if (from.HasValue)
            query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.CreatedAt <= to.Value);
        return query;
    }
}
