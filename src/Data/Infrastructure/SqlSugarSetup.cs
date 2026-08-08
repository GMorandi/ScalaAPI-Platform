using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using ScalaAPI.Data.Entities;

namespace ScalaAPI.Data.Infrastructure;

public static class SqlSugarSetup
{
    public static IServiceCollection AddSqlSugarData(this IServiceCollection services,
        string connectionString)
    {
        services.AddScoped<ISqlSugarClient>(_ => new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        }));

        services.AddSingleton<BatchWriter<UsageLogEntity>>(sp =>
        {
            var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = DbType.PostgreSQL,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
            var logger = sp.GetRequiredService<ILogger<BatchWriter<UsageLogEntity>>>();
            return new BatchWriter<UsageLogEntity>(db, logger);
        });

        return services;
    }

    public static void EnsureTables(ISqlSugarClient db)
    {
        db.CodeFirst.InitTables<UsageLogEntity>();
    }
}
