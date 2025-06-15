namespace Argon.Features.EF;

using System.Data.Common;
using Api.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            var credentialsProvider = sp.GetRequiredService<IVaultDbCredentialsProvider>();
            var connStr             = credentialsProvider.BuildConnectionString();

            options.EnableDetailedErrors()
               .EnableSensitiveDataLogging()
               .UseNpgsql(connStr, npgsql => npgsql.ConfigureDataSource(q => q.EnableDynamicJson().UseJsonNet()))
               .ReplaceService<IHistoryRepository, YugabyteHistoryRepository>()
               .AddInterceptors(new TimeStampAndSoftDeleteInterceptor(),
                    new RotatableConnectionInterceptor(credentialsProvider));
        }, 512);
    }
}


public class RotatableConnectionInterceptor(IVaultDbCredentialsProvider credentialsProvider) : DbConnectionInterceptor
{
    public async override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = new())
    {
        var r = await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);

        if (connection is not NpgsqlConnection npgsql || !credentialsProvider.IsEnabled) 
            return r;

        var originalBuilder = new NpgsqlConnectionStringBuilder(npgsql.ConnectionString);
        var current         = await credentialsProvider.GetCredentialsAsync();

        originalBuilder.Username = current.username;
        originalBuilder.Password = current.password;

        npgsql.ConnectionString = originalBuilder.ConnectionString;

        return r;
    }
}