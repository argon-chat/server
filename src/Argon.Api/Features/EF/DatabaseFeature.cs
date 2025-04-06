namespace Argon.Features.EF;

using Api.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

public static class DatabaseFeature
{
    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder) where T : DbContext
        => builder.Services.AddPooledDbContextFactory<T>(options =>
        {
            options.EnableDetailedErrors()
               .EnableSensitiveDataLogging()
               .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
                    optionsBuilder => optionsBuilder.ConfigureDataSource(q => q.EnableDynamicJson().UseJsonNet()))
               .ReplaceService<IHistoryRepository, YugabyteHistoryRepository>()
               .AddInterceptors(new TimeStampAndSoftDeleteInterceptor());
        });
}