namespace Argon.Core.Features.EF;

using Argon.Features.EF;
using Argon.Features.Clustering;
using Argon.Features.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

public static class WarmUpExtension
{
    extension(WebApplication app)
    {
        public async Task<WebApplication> WarmUp<T>(bool isMigrate = true) where T : DbContext
        {
            var role = app.Services.GetRequiredService<RoleDescriptor>();

            if (role.IsClient)
                return app;

            using var scope = app.Services.CreateScope();

            var             factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<T>>();
            await using var db      = await factory.CreateDbContextAsync();

            if (isMigrate)
                await db.MigrateArgonDatabase(
                    scope.ServiceProvider.GetRequiredService<ILogger<T>>(),
                    scope.ServiceProvider.GetService<DatabaseProvider>()?.Kind ?? DatabaseProviderKind.CockroachDb,
                    SchemaReconcileOptions.FromConfiguration(app.Configuration),
                    // Resolved rather than required, and it degrades rather than throws: the singleton
                    // arrives with AddSchemaReconcileDiagnostics, and a host that has not registered it
                    // should still get the pass and its log lines — only the /health surface goes
                    // missing. A required service here would turn "the diagnostic is not wired" into
                    // "the process will not boot".
                    scope.ServiceProvider.GetService<SchemaReconcileState>() ?? new SchemaReconcileState(),
                    role.Id.Value);
            else
                await db.Database.EnsureCreatedAsync();
            return app;
        }

        public async Task<WebApplication> WarmUpRotations()
        {
            if (app.Services.GetRequiredService<RoleDescriptor>().IsClient)
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
                -- TEXT rather than Cockroach's STRING: both engines accept TEXT, and the same
                -- bootstrap DDL has to run against vanilla PostgreSQL in tests/local dev.
                locked_by TEXT,
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

    /// <summary>
    /// Creates the database, and turns the one failure an operator cannot read into one they can.
    /// </summary>
    /// <remarks>
    /// <para>This is not a bare <c>CREATE DATABASE</c>. <c>OnModelCreating</c> annotates the model with
    /// <c>Regional:MultiRegion</c> unconditionally, so on CockroachDB the multiregional generator turns
    /// it into <c>CREATE DATABASE … PRIMARY REGION … REGIONS … SURVIVE …</c>. CockroachDB only has
    /// region names because some node was started with <c>--locality=region=…</c>; against a cluster
    /// provisioned without them, the first statement of the first boot is rejected, not one migration
    /// applies, and the pod restarts into the same failure forever. Deployments that provision the
    /// database themselves never take this branch at all, which is why it went unnoticed.</para>
    ///
    /// <para>Cockroach's own message for that names the region, which points the operator at the
    /// database and not at the node flags that are actually wrong. Naming the requirement — and the
    /// regions this process is configured with — is the difference between a five-minute fix and an
    /// outage spent reading EF Core source. The server error is kept as the inner exception, so nothing
    /// is hidden; the wrapper only decides what the first line says.</para>
    ///
    /// <para>Deliberately not narrowed by SQLSTATE. A wrong code would silently restore the unreadable
    /// message, and there is no second common reason for this particular statement to be refused, so
    /// the text is worded to stay honest when the cause turns out to be something else.</para>
    /// </remarks>
    private async static Task CreateDatabaseAsync(
        DbContext dbCtx,
        IRelationalDatabaseCreator creator,
        ILogger logger,
        DatabaseProviderKind providerKind)
    {
        try
        {
            await creator.CreateAsync();
            logger.LogInformation("Database created");
        }
        catch (PostgresException e) when (providerKind is DatabaseProviderKind.CockroachDb &&
                                          ReadMultiRegion(dbCtx) is { Primary.Length: > 0 } multiRegion)
        {
            var declared = string.Join(", ", new[] { multiRegion.Primary }
               .Concat(multiRegion.Regions)
               .Distinct(StringComparer.OrdinalIgnoreCase));

            throw new InvalidOperationException(
                $"CockroachDB refused CREATE DATABASE … PRIMARY REGION \"{multiRegion.Primary}\" " +
                $"SURVIVE {multiRegion.Survive}. A cluster only has regions if its nodes were started with " +
                $"--locality=region=<name>, and every region named in Database:Regions ({declared}) has to " +
                $"appear in SHOW REGIONS FROM CLUSTER before a database can be created with it. Either give " +
                $"the nodes matching localities, or set Database:Regions:PrimaryRegion to an empty value to " +
                $"create an ordinary single-region database. Server said: {e.MessageText}", e);
        }
    }

    /// <summary>The multi-region declaration the model carries, or <c>null</c> if it carries none.</summary>
    private static MultiRegionAnnotation? ReadMultiRegion(DbContext dbCtx)
        => dbCtx.Model.FindAnnotation("Regional:MultiRegion")?.Value is string payload
            ? JsonConvert.DeserializeObject<MultiRegionAnnotation>(payload)
            : null;

    private async static Task MigrateArgonDatabase<T>(
        this T dbCtx,
        ILogger<T> logger,
        DatabaseProviderKind providerKind,
        SchemaReconcileOptions reconcile,
        SchemaReconcileState reconcileState,
        string roleId)
        where T : DbContext
    {
        var db = dbCtx.Database;

        var dbCreator = db.GetService<IRelationalDatabaseCreator>();
        if (!await dbCreator.ExistsAsync())
            await CreateDatabaseAsync(dbCtx, dbCreator, logger, providerKind);

        // Pin one physical CockroachDB session for the whole migration. The bootstrap tables we
        // CREATE here (__MigrationLock, __EFMigrationsHistory) must be visible — same database,
        // same schema/search_path — to the very next statement that uses them. db.ExecuteSqlRawAsync
        // and command.ExecuteNonQueryAsync otherwise each open/close the ref-counted connection
        // independently and can land on different pooled sessions; on a brand-new database that
        // races a CREATE against its first use and surfaces as
        // 42P01: relation "__EFMigrationsHistory" does not exist. The connection is released when
        // the warm-up DbContext is disposed.
        await db.OpenConnectionAsync();

        // Now that there is a connection, check that the engine is the one configuration claimed. Here
        // rather than earlier because this is the first point where a connection exists and is pinned,
        // and before the shims and the migration loop because both of them branch on the declared kind.
        await DatabaseEngineProbe.VerifyAsync(db.GetDbConnection(), providerKind, logger);

        // Migrations scaffolded against CockroachDB reference its built-ins (unique_rowid) in column
        // defaults. Define equivalents before the first migration runs so vanilla PostgreSQL can
        // replay exactly the same history.
        if (providerKind is DatabaseProviderKind.PostgreSql)
            await PostgresCompatibilityShims.ApplyAsync(dbCtx, logger);

        var lockTtl  = TimeSpan.FromMinutes(10);
        var workerId = Environment.MachineName;

        if (!await TryAcquireMigrationLockAsync(dbCtx, logger, workerId, lockTtl))
        {
            logger.LogWarning("Another worker is performing migration. Skipping.");

            // Recorded rather than left at NotRun, because those two say different things. This pod
            // did not look, which is not evidence that anything is converged — and a health surface
            // that cannot tell "nobody checked" from "everything matches" is the failure this whole
            // design refuses to allow.
            reconcileState.Publish(new SchemaReconcileReport(
                SchemaReconcileVerdict.SkippedLock,
                "another worker holds the migration lock; the schema was not read",
                SchemaTtlPlan.Empty, [], DateTimeOffset.UtcNow));

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
                logger.LogInformation("No pending migrations.");
            else
                await ApplyMigrationsAsync(dbCtx, logger, pending);

            // Reconciled here, and the shape of this branch is the whole reason it works. What used to
            // be an early `return` on "no pending migrations" is now an `else`, because annotation-only
            // model changes emit no migration operations at all — which means a deployed database has
            // `pending.Count == 0` on every boot, forever. A reconciler behind that return would run on
            // fresh databases, where CREATE TABLE already carries the TTL clause and there is nothing
            // to fix, and never on the one database it exists for. Green in tests, green on every fresh
            // deployment, silent no-op in production. Do not put the return back.
            // Guarded separately from the migrations above, and never rethrowing. Migrations failing
            // must stop the pod — it would serve against a schema it does not agree with. This is a
            // diagnostic that mostly reads, and until now nothing after the pending-migrations check
            // could fail a boot at all: the steady state returned early. Letting a schema catalog read,
            // a payload that no longer parses, or a dropped connection take down every silo in the
            // fleet would make an observability feature the least reliable thing in the process.
            //
            // The verdict still travels — a failed pass is published, counted and visible on /health,
            // which is where an operator should learn about it rather than from a crash loop.
            try
            {
                reconcileState.Publish(await SchemaReconciler.RunAsync(
                    dbCtx,
                    db.GetDbConnection(),
                    reconcile,
                    // The boot path may never do more than re-pace a TTL that is already running. Turning
                    // one on schedules deletion of every already-expired row, and dozens of pods arrive on
                    // this path at the same instant during a hard reboot; that statement belongs to an
                    // operator with a maintenance window, not to a pod that happened to win a lease.
                    SchemaChangeTier.Automatic,
                    roleId,
                    logger));
            }
            catch (Exception e)
            {
                logger.LogError(e, "Schema reconcile failed; the database is unchanged and the pod continues");
                reconcileState.Publish(SchemaReconcileReport.Faulted(e));
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

    /// <summary>
    /// Applies the pending migrations, one auto-committed statement at a time.
    /// </summary>
    /// <remarks>
    /// Lifted out of <c>MigrateArgonDatabase</c> without a line of it changing, so that "no pending
    /// migrations" could stop being an early <c>return</c> — see the reconcile call site for why that
    /// return was load-bearing in the wrong direction. Still called only while the migration lock is
    /// held; the caller owns acquiring and releasing it.
    /// </remarks>
    private async static Task ApplyMigrationsAsync<T>(T dbCtx, ILogger<T> logger, List<string> pending)
        where T : DbContext
    {
        var db = dbCtx.Database;

        var historyRepo        = db.GetService<IHistoryRepository>();
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
}