namespace Argon.Core.Features.EF;

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

        var dbCreator = db.GetService<IRelationalDatabaseCreator>();
        if (!await dbCreator.ExistsAsync())
        {
            await dbCreator.CreateAsync();
            logger.LogInformation("Database created");
        }

        // Pin one physical CockroachDB session for the whole migration. The bootstrap tables we
        // CREATE here (__MigrationLock, __EFMigrationsHistory) must be visible — same database,
        // same schema/search_path — to the very next statement that uses them. db.ExecuteSqlRawAsync
        // and command.ExecuteNonQueryAsync otherwise each open/close the ref-counted connection
        // independently and can land on different pooled sessions; on a brand-new database that
        // races a CREATE against its first use and surfaces as
        // 42P01: relation "__EFMigrationsHistory" does not exist. The connection is released when
        // the warm-up DbContext is disposed.
        await db.OpenConnectionAsync();

        var lockTtl  = TimeSpan.FromMinutes(10);
        var workerId = Environment.MachineName;

        if (!await TryAcquireMigrationLockAsync(dbCtx, logger, workerId, lockTtl))
        {
            logger.LogWarning("Another worker is performing migration. Skipping.");
            return;
        }

        try
        {
            var historyRepo = db.GetService<IHistoryRepository>();

            // The history table must exist before we can record applied migrations.
            // GetCreateIfNotExistsScript is idempotent, so we run it unconditionally rather than
            // trusting a separate ExistsAsync probe that, on a freshly created database, can
            // momentarily disagree with the session we actually write through.
            await db.ExecuteSqlRawAsync(historyRepo.GetCreateIfNotExistsScript());

            var pending = (await db.GetPendingMigrationsAsync()).ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("No pending migrations.");
                return;
            }

            var migrationsAssembly = db.GetService<IMigrationsAssembly>();
            var sqlGenerator       = db.GetService<IMigrationsSqlGenerator>();
            var connection         = db.GetService<IRelationalConnection>();
            var modelInitializer   = db.GetService<IModelRuntimeInitializer>();
            var activeProvider     = db.ProviderName!;
            var productVersion     = typeof(Migration).Assembly.GetName().Version?.ToString() ?? "";

            foreach (var migrationId in pending)
            {
                var migration = migrationsAssembly.CreateMigration(
                    migrationsAssembly.Migrations[migrationId], activeProvider);

                // CockroachDB forbids mixing DDL with DML, and multiple schema changes,
                // inside a single transaction. The old approach generated one SQL script
                // per migration and ran it through a single ExecuteSqlRaw — i.e. one
                // implicit transaction — so a scaffolded "ADD COLUMN; ADD COLUMN; UPDATE"
                // aborted halfway and left the table in a state the non-idempotent re-run
                // couldn't recover from. Instead we execute each statement on its own, so
                // every statement auto-commits independently (the only Cockroach-safe way:
                // ADD COLUMN commits, then a later UPDATE sees the now-public column), and
                // we write the history row only after all of a migration's commands apply.
                // The SQL generator needs a FINALIZED model. Seed-data operations
                // (UpdateData / InsertData / DeleteData) call IModel.GetRelationalModel(), which
                // only works once the model's runtime dependencies are initialized.
                // migration.TargetModel is the design-time snapshot, so finalize it first — exactly
                // as EF's own Migrator.FinalizeModel does — otherwise any migration carrying HasData
                // changes throws "The model must be finalized and its runtime dependencies must be
                // initialized before 'GetRelationalModel' can be used."
                var targetModel = migration.TargetModel is null
                    ? null
                    : modelInitializer.Initialize(migration.TargetModel);

                var commands = sqlGenerator.Generate(
                    migration.UpOperations, targetModel, MigrationsSqlGenerationOptions.Default);

                foreach (var command in commands)
                    await command.ExecuteNonQueryAsync(connection);

                await db.ExecuteSqlRawAsync(historyRepo.GetInsertScript(new HistoryRow(migrationId, productVersion)));
                logger.LogInformation("Applied migration {Migration}", migrationId);
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