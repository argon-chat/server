namespace Argon.Features.EF;

using Api.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Vault;

public static class DatabaseFeature
{
    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder) where T : DbContext
    {
        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
        builder.Services.AddSingleton<IVaultDbCredentialsProvider, VaultDbCredentialsProvider>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IVaultDbCredentialsProvider>());
        builder.Services.AddPooledDbContextFactory<T>((sp, options) =>
        {
            var connStr = sp.GetRequiredService<IVaultDbCredentialsProvider>().BuildConnectionString();

            options.EnableDetailedErrors()
               .EnableSensitiveDataLogging()
               .UseNpgsql(connStr, npgsql => {
                    npgsql.ConfigureDataSource(q => q.EnableDynamicJson().UseJsonNet());
                })
               .ReplaceService<IHistoryRepository, YugabyteHistoryRepository>()
               .AddInterceptors(new TimeStampAndSoftDeleteInterceptor());
        });
    }
}

