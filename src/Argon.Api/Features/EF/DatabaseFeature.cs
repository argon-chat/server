namespace Argon.Features.EF;

using Api.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;

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
}