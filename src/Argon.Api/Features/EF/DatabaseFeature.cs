namespace Argon.Features.EF;

using ClickHouse.EntityFrameworkCore.Extensions;

public static class DatabaseFeature
{
    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder) where T : DbContext
        => builder.Services.AddPooledDbContextFactory<T>(x => x
           .EnableDetailedErrors().EnableSensitiveDataLogging().UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
           .AddInterceptors(new TimeStampAndSoftDeleteInterceptor()));


    public static void AddPooledClickhouse<T>(this WebApplicationBuilder builder) where T : DbContext
        => builder.Services.AddPooledDbContextFactory<T>(x => x
           .EnableDetailedErrors().EnableSensitiveDataLogging().UseClickHouse(builder.Configuration.GetConnectionString("clickhouse"))
           .AddInterceptors(new TimeStampAndSoftDeleteInterceptor()));


}