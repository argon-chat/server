namespace Argon.Features.EF;

using Argon.Core.Features.EF;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Vault;

public static class DatabaseFeature
{
    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder) where T : DbContext
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
        DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
        builder.Services.AddSingleton<IVaultDbCredentialsProvider, VaultDbCredentialsProvider>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IVaultDbCredentialsProvider>());
        builder.Services.Configure<DatabaseRegionOptions>(builder.Configuration.GetSection("Database:Regions"));
        builder.Services.AddPooledDbContextFactory<T>((_, options) =>
        {
            options.EnableDetailedErrors()
               .EnableSensitiveDataLogging()
               
               .UseNpgsql(builder.Configuration.GetConnectionString("Default"), npgsql =>
                {
                    npgsql.UseNodaTime();
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(2),
                        errorCodesToAdd: ["40001"]);
                    npgsql.MaxBatchSize(50);
                    npgsql.ConfigureDataSource(q => q.EnableDynamicJson().UseJsonNet());
                    npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                })
               .ReplaceService<IHistoryRepository, NoLockHistoryRepository>()
               .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
               .AddInterceptors(new TimeStampAndSoftDeleteInterceptor())
               .UseMultiregionalCompatibility();
        }, 512);
    }
}

public class DatabaseRegionOptions
{
    public required string PrimaryRegion { get; set; }
    public required string[] ReplicateRegion { get; set; }
}