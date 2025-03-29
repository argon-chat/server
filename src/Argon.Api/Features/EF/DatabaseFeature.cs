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
               .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
               .ReplaceService<IHistoryRepository, YugabyteHistoryRepository>()
               .AddInterceptors(new TimeStampAndSoftDeleteInterceptor());
        });


    public static void AddPooledClickhouse<T>(this WebApplicationBuilder builder) where T : DbContext
        => builder.Services.AddPooledDbContextFactory<T>(x => x
           .EnableDetailedErrors().EnableSensitiveDataLogging().UseClickHouse(builder.Configuration.GetConnectionString("clickhouse"))
           .AddInterceptors(new TimeStampAndSoftDeleteInterceptor()));
}