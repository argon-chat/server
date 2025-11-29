namespace Argon.Core.Features.EF;

using Argon.Features.EF;
using Argon.Features.Env;
using Argon.Features.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

public static class WarmUpExtension
{
    extension(WebApplication app)
    {
        public async Task<WebApplication> WarmUp<T>(bool isMigrate = true) where T : DbContext
        {
            if (app.Environment.IsEntryPoint())
                return app;

            using var scope = app.Services.CreateScope();

            var             factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<T>>();
            await using var db      = await factory.CreateDbContextAsync();

            if (isMigrate)
                await db.MigrateCockroach(scope.ServiceProvider.GetRequiredService<ILogger<T>>());
            else
                await db.Database.EnsureCreatedAsync();
            return app;
        }

        public async Task<WebApplication> WarmUpCassandra()
        {
            if (app.Environment.IsEntryPoint())
                return app;

            using var scope = app.Services.CreateScope();

            var controller = scope.ServiceProvider.GetRequiredService<CassandraMigrationController>();

            await controller.BeginMigrations();

            return app;
        }

        public async Task<WebApplication> WarmUpRotations()
        {
            if (app.Environment.IsEntryPoint())
                return app;

            using var scope = app.Services.CreateScope();

            var rotationManager = scope.ServiceProvider.GetRequiredService<IVaultDbCredentialsProvider>();
            await rotationManager.EnsureLoadedAsync();
            return app;
        }
    }

    private async static Task<bool> TryAcquireMigrationLockAsync(
        DbContext db,
        ILogger logger,
        string workerId,
        TimeSpan ttl)
    {
        var now     = DateTime.UtcNow;
        var expires = now.Add(ttl);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__MigrationLock" (
                id INT PRIMARY KEY DEFAULT 1,
                locked_at TIMESTAMPTZ,
                locked_by STRING,
                expires_at TIMESTAMPTZ
            );
            """);

        var inserted = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "__MigrationLock" (id, locked_at, locked_by, expires_at)
                VALUES (1, now(), {0}, {1})
                ON CONFLICT (id) DO NOTHING;
            """, workerId, expires);

        if (inserted == 1)
            return true;

        var updated = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "__MigrationLock"
            SET locked_at = now(),
                locked_by = {0},
                expires_at = {1}
            WHERE id = 1 AND expires_at < now();
            """, workerId, expires);


        if (updated == 1)
        {
            logger.LogInformation("Migration lock acquired via UPDATE by {Worker}", workerId);
            return true;
        }

        logger.LogWarning("Migration lock busy, held by another worker.");
        return false;
    }

    private async static Task ReleaseMigrationLockAsync(DbContext db, ILogger logger)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM "__MigrationLock" WHERE id = 1;
            """);
        logger.LogInformation("Migration lock released");
    }

    private async static Task MigrateCockroach<T>(this T dbCtx, ILogger<T> logger) where T : DbContext
    {
        var db = dbCtx.Database;

        var migrations = await db.GetPendingMigrationsAsync();
        var migrator   = db.GetService<IMigrator>();
        var dbCreator  = db.GetService<IRelationalDatabaseCreator>();

        if (!await dbCreator.ExistsAsync())
        {
            await dbCreator.CreateAsync();
            logger.LogInformation("Database created");
        }

        var lockTtl  = TimeSpan.FromMinutes(10);
        var workerId = Environment.MachineName;

        if (!await TryAcquireMigrationLockAsync(dbCtx, logger, workerId, lockTtl))
        {
            logger.LogWarning("Another worker is performing migration. Skipping.");
            return;
        }

        var applied = await db.GetAppliedMigrationsAsync();
        var pending = await db.GetPendingMigrationsAsync();

        if (!pending.Any())
        {
            logger.LogInformation("No pending migrations.");
            return;
        }

        var current = applied.LastOrDefault();

        try
        {
            foreach (var nextMigrationId in migrations)
            {
                var sql = migrator.GenerateScript(
                    fromMigration: current,
                    toMigration: nextMigrationId,
                    options: MigrationsSqlGenerationOptions.Script
                );
                await db.ExecuteSqlRawAsync(sql);
                logger.LogInformation("Applied migration {Migration}", nextMigrationId);
                current = nextMigrationId;
            }
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed apply migrations");
            throw;
        }
        finally
        {
            await ReleaseMigrationLockAsync(dbCtx, logger);
        }
    }
}