namespace Argon.Core.Features.EF;

using Argon.Features.EF;
using Argon.Features.Env;
using Argon.Features.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public static class WarmUpExtension
{
    public static WebApplication WarmUp<T>(this WebApplication app, bool isMigrate = true) where T : DbContext
    {
        if (app.Environment.IsEntryPoint())
            return app;

        using var scope = app.Services.CreateScope();

        var       factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<T>>();
        using var db      = factory.CreateDbContext();

        if (isMigrate)
            Migrate(db, scope.ServiceProvider.GetRequiredService<ILogger<T>>(), scope.ServiceProvider);
        else
            db.Database.EnsureCreated();
        return app;
    }

    public async static Task<WebApplication> WarmUpCassandra(this WebApplication app)
    {
        if (app.Environment.IsEntryPoint())
            return app;

        using var scope = app.Services.CreateScope();

        var controller = scope.ServiceProvider.GetRequiredService<CassandraMigrationController>();

        await controller.BeginMigrations();

        return app;
    }

    public async static Task<WebApplication> WarmUpRotations(this WebApplication app)
    {
        if (app.Environment.IsEntryPoint())
            return app;

        using var scope = app.Services.CreateScope();

        var rotationManager = scope.ServiceProvider.GetRequiredService<IVaultDbCredentialsProvider>();
        await rotationManager.EnsureLoadedAsync();
        return app;
    }

    private static void Migrate<T>(T dbCtx, ILogger<T> logger, IServiceProvider provider) where T : DbContext
    {
        var migrations = dbCtx.Database.GetPendingMigrations().ToList();
        foreach (var migrationId in migrations)
        {
            logger.LogInformation("Applying migration: {migration}", migrationId);

            var beforeHandler = provider.GetKeyedService<IBeforeMigrationsHandler>(IBeforeMigrationsHandler.Key(migrationId.Split('_').Last()));

            beforeHandler?.BeforeMigrateAsync(dbCtx).Wait(TimeSpan.FromMinutes(5));

            try
            {
                dbCtx.Database.Migrate(migrationId);
            }
            catch (InvalidOperationException e) when (e.Message.Contains("NpgsqlTransaction has completed"))
            {
            }

            logger.LogInformation("Migration applied: {migration}", migrationId);
        }
    }
}