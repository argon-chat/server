namespace Argon.Features.EF;

using Api.Migrations;
using ClickHouse.EntityFrameworkCore.Extensions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DatabaseFeature
{
    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder) where T : DbContext
        => builder.Services.AddPooledDbContextFactory<T>(options =>
        {
            options.EnableDetailedErrors()
               .EnableSensitiveDataLogging()
               .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"), opt =>
                {
                    //opt.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
                    //opt.MaxBatchSize(1);
                })
               .ReplaceService<IHistoryRepository, YugabyteHistoryRepository>()
               //.ReplaceService<IMigrationCommandExecutor, NoTransactionMigrationCommandExecutor>()
               .AddInterceptors(new TimeStampAndSoftDeleteInterceptor());
        });


    public static void AddPooledClickhouse<T>(this WebApplicationBuilder builder) where T : DbContext
        => builder.Services.AddPooledDbContextFactory<T>(x => x
           .EnableDetailedErrors().EnableSensitiveDataLogging().UseClickHouse(builder.Configuration.GetConnectionString("clickhouse"))
           .AddInterceptors(new TimeStampAndSoftDeleteInterceptor()));
}