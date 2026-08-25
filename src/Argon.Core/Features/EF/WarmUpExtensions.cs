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
    /// <summary>
    /// The row that serialises migrations across the fleet — and it is deliberately not the row that
    /// used to.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the lock that was here is gone.</b> This path hand-rolled its own lock over
    /// <c>__MigrationLock</c>, and that lock had four defects: <c>expires_at</c> computed from the
    /// pod's <c>DateTime.UtcNow</c> but compared against the server's <c>now()</c>, so clock skew moved
    /// the TTL in both directions; a release of <c>DELETE … WHERE id = 1</c> with no owner predicate,
    /// running in a <c>finally</c>, so a worker whose lease had already been stolen deleted the
    /// <em>new</em> holder's row on its way out and admitted a third; no fence, so a holder that
    /// stalled and resumed could not discover it no longer owned anything; and no renewal on a
    /// ten-minute TTL, so a longer migration lost the lock mid-flight and the release defect turned
    /// that into two workers applying migrations at once. <see cref="SchemaReconcileLease"/> already
    /// fixes all four, and the TTL sweeper already depends on it. Two implementations of a distributed
    /// lock in one repository is the defect, not the fix.</para>
    ///
    /// <para><b>Why a new table, which is the load-bearing decision here.</b> <c>__MigrationLock</c>
    /// exists in every deployed database with columns <c>(id, locked_at, locked_by, expires_at)</c> and
    /// no <c>fence</c>. The lease bootstraps with <c>CREATE TABLE IF NOT EXISTS</c>, which does not add
    /// a column to a table that is already there — so pointing the lease at the old name would give
    /// every pod, of every role, on the boot path, at the same instant, an <c>INSERT</c> naming a
    /// column the live table does not have: <c>42703</c> before the first migration, fleet-wide. The
    /// alternative considered was <c>ALTER TABLE … ADD COLUMN IF NOT EXISTS fence</c> ahead of the
    /// bootstrap, which puts a schema migration for the lock <em>inside</em> the boot path the lock
    /// exists to protect, issued concurrently by every booting pod, before any lock is held. A second
    /// table costs one dead row in one dead table that an operator can
    /// <c>DROP TABLE "__MigrationLock"</c> whenever they like; it buys a boot path that carries no
    /// <c>ALTER</c>. Not no DDL — the lease's own <c>CREATE TABLE IF NOT EXISTS</c> runs there, and on
    /// the one rollout that creates this table it really executes rather than no-opping, so every pod
    /// races every other exactly as the old lock's bootstrap did when it was introduced. That race is
    /// survivable and one deploy wide; an <c>ALTER</c> racing itself on the path the lock protects, on
    /// every deploy thereafter, is the thing being refused.
    /// </para>
    ///
    /// <para><b>What that costs, said out loud.</b> Across the single deploy that crosses this change,
    /// a pod on the old build takes <c>__MigrationLock</c> while a pod on the new build takes this one,
    /// and the two do not exclude each other. That window needs an old pod to <em>boot</em> rather than
    /// merely still be running, it lasts one rollout, and the old build's unqualified <c>DELETE</c>
    /// means it did not reliably exclude anything anyway. Sharing one table would not have closed the
    /// window either: the old build releases by <c>id</c> alone, so it would delete a new build's row
    /// whatever name the two agreed on.</para>
    ///
    /// <para>Public because <c>MigrationLeaseTests</c> asserts on it — that it is a plain identifier the
    /// lease will accept, and that it is neither of the other two lease tables. Three resources, three
    /// rows: a job that takes somebody else's row locks them out of something it does not touch, which
    /// is a wrong answer rather than a stronger lock. The TTL sweeper's hourly delete pass and a pod
    /// trying to migrate are the pair that makes that concrete.</para>
    /// </remarks>
    public const string MigrationLeaseTable = "__MigrationLease";

    /// <summary>
    /// Ten minutes, unchanged in value and changed entirely in meaning.
    /// </summary>
    /// <remarks>
    /// Without renewal this was a ceiling on the <em>whole</em> migration run — anything slower lost the
    /// lock mid-flight — which is why it had to be this large. With
    /// <see cref="SchemaReconcileLease.TryRenewAsync"/> called before every statement it is instead a
    /// ceiling on one statement, and simultaneously how long a pod that died holding the lease blocks
    /// everybody else. Those two pull in opposite directions and both want this order of magnitude, so
    /// the number stayed rather than being retuned on a fresh guess. The residual gap is one statement
    /// running longer than this — a backfill, an index build over a large table — and what makes that
    /// survivable rather than silent is that the next renewal fails and
    /// <see cref="ApplyMigrationsAsync"/> stops instead of continuing under a tenure that ended.
    /// </remarks>
    private static readonly TimeSpan MigrationLeaseLifetime = TimeSpan.FromMinutes(10);

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
                    SchemaDeclarations.IsDryRun(app.Configuration),
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
        bool dryRunDeclarations,
        string roleId)
        where T : DbContext
    {
        var db = dbCtx.Database;

        var dbCreator = db.GetService<IRelationalDatabaseCreator>();
        if (!await dbCreator.ExistsAsync())
            await CreateDatabaseAsync(dbCtx, dbCreator, logger, providerKind);

        // Pin one physical CockroachDB session for the whole migration. The bootstrap tables we
        // CREATE here (__MigrationLease, __EFMigrationsHistory) must be visible — same database,
        // same schema/search_path — to the very next statement that uses them. db.ExecuteSqlRawAsync
        // and command.ExecuteNonQueryAsync otherwise each open/close the ref-counted connection
        // independently and can land on different pooled sessions; on a brand-new database that
        // races a CREATE against its first use and surfaces as
        // 42P01: relation "__EFMigrationsHistory" does not exist. The connection is released when
        // the warm-up DbContext is disposed.
        //
        // The lease below borrows the same pinned connection for the same reason, and for a second
        // one: a lease renewed on a different session than the statements it protects protects
        // nothing once the pool hands that session to somebody else.
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

        // roleId rather than the Environment.MachineName this used to pass. The lease builds its holder
        // from machine/role/pid/boot-guid, and that is what makes "am I still the owner" answerable at
        // all: docker-compose and local dev run several roles as processes on one host, so the machine
        // name is shared between them, and a pid is reused after a restart.
        //
        // `await using` rather than the `finally` this used to release from. Same coverage — the lease
        // is given up on the throwing path too — but the release now carries the holder and fence
        // predicates, so a worker whose tenure already ended deletes nothing instead of deleting
        // whoever took over from it and admitting a third.
        await using var lease = await SchemaReconcileLease.TryAcquireAsync(
            db.GetDbConnection(), logger, roleId, MigrationLeaseLifetime, MigrationLeaseTable);

        if (lease is null)
        {
            // And the declaration step below is skipped with it, deliberately. Whoever holds the lease
            // is running the same statements from the same model a few lines later; a second pod
            // issuing them concurrently would stack schema changes on one table for no gain.
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
                logger.LogInformation("No pending migrations.");
            else
                await ApplyMigrationsAsync(dbCtx, logger, lease, pending);

            // The declarations are issued here, and the shape of the branch above is the whole reason
            // this works. What used to be an early `return` on "no pending migrations" is an `else`,
            // because an annotation-only model change emits no migration operation at all — so a
            // deployed database has `pending.Count == 0` on every boot, forever. A step behind that
            // return would run on fresh databases, where CREATE TABLE already carries both clauses and
            // there is nothing to fix, and never on the one database it exists for: green in tests,
            // green on every fresh deployment, silent no-op in production. Do not put the return back.
            //
            // Guarded separately from the migrations above, and never rethrowing. Migrations failing
            // must stop the pod — it would serve against a schema it does not agree with. This must
            // not: it changes where rows live and when they expire, and neither answer makes the
            // process unable to serve. Letting a rejected ALTER take down every silo in the fleet would
            // make table placement the least reliable thing in the process.
            //
            // The migration lease is still held, which is what keeps a hard reboot from turning this
            // into every pod issuing the same schema changes at once. It is deliberately not threaded
            // into the step: renewing per statement would tie a plain model-to-SQL routine to the lease
            // type and make it untestable without one, and the window it would close is the handful of
            // seconds between the last renewal above and the last ALTER below.
            try
            {
                await SchemaDeclarations.ApplyAsync(dbCtx, db.GetDbConnection(), dryRunDeclarations, logger);
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "Applying the model's table declarations failed; the pod continues and the next boot " +
                    "will try again");
            }
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed apply migrations");
            throw;
        }
    }

    /// <summary>
    /// Applies the pending migrations, one auto-committed statement at a time, and keeps the lease
    /// alive across all of them.
    /// </summary>
    /// <remarks>
    /// Lifted out of <c>MigrateArgonDatabase</c> so that "no pending migrations" could stop being an
    /// early <c>return</c> — see the <see cref="SchemaDeclarations.ApplyAsync"/> call site for why that
    /// return was load-bearing in the wrong direction. The caller still owns acquiring and releasing the lease; what this owns is
    /// renewing it, because the caller cannot: the loop below is the only thing that knows where the
    /// safe renewal points are.
    /// </remarks>
    private async static Task ApplyMigrationsAsync<T>(
        T dbCtx, ILogger<T> logger, SchemaReconcileLease lease, List<string> pending)
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

        var completed = 0;

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

            for (var index = 0; index < commands.Count; index++)
            {
                // Renewed immediately before every statement, which is the only place a renewal can go:
                // the lease has no background heartbeat, because a heartbeat needs a second connection
                // and Npgsql will not run two commands on one — see SchemaReconcileLease. Every
                // statement below auto-commits, so this is also the boundary at which stopping leaves
                // the database somewhere the next boot can carry on from.
                //
                // A renewal that fails means the tenure ended and somebody else holds the lease, which
                // is to say another pod is applying these same migrations right now. Stopping is the
                // whole point of this change: continuing is the concurrent application everything above
                // exists to prevent. Do not soften this into a log line and a carry-on.
                if (!await lease.TryRenewAsync())
                    throw LeaseLost(lease, migrationId, index, commands.Count, completed);

                await commands[index].ExecuteNonQueryAsync(connection);
            }

            // And once more before the history row, which is a statement like any other and the one that
            // decides whether this migration is ever re-run. Renewing only *before* each generated
            // statement leaves the last one uncovered: a tenure that ended while it executed would still
            // reach this line and record the migration as applied — on behalf of a lease somebody else
            // now holds, and possibly while that somebody is applying the same statements. The gap is
            // one statement wide and it is the statement that makes the outcome permanent.
            if (!await lease.TryRenewAsync())
                throw LeaseLost(lease, migrationId, commands.Count, commands.Count, completed);

            await db.ExecuteSqlRawAsync(historyRepo.GetInsertScript(new HistoryRow(migrationId, productVersion)));

            completed++;
            logger.LogInformation("Applied migration {Migration}", migrationId);
        }
    }

    /// <summary>
    /// The stop a lost lease turns into, worded for whoever finds the pod refusing to start.
    /// </summary>
    /// <remarks>
    /// <para>Thrown rather than logged-and-continued, and thrown rather than swallowed-and-booted. It
    /// travels up through <c>MigrateArgonDatabase</c>'s <c>LogCritical</c>, out of warm-up, and the
    /// process does not start — which is already the contract for a migration that fails, and this is
    /// one: the schema is part-way between two versions and this pod does not agree with it. The crash
    /// loop resolves itself without help, because the next boot finds the lease held, skips migrating
    /// and starts normally as soon as the other worker is done. Note what is deliberately <em>not</em>
    /// reused here: the busy-lease path above publishes <c>SkippedLock</c> and boots, and that stays
    /// correct only because nothing had been applied at the point it decided.</para>
    ///
    /// <para>No extra log line. <see cref="SchemaReconcileLease.TryRenewAsync"/> already warns with the
    /// table, the holder and the fence at the moment it finds out, and the caller's <c>LogCritical</c>
    /// carries this message in full; a third line would only make one event look like three.</para>
    ///
    /// <para>The two positions are worded differently because they need different things from the
    /// reader. Before a migration's first statement, nothing of it was issued and the next boot resumes
    /// cleanly. After it, some statements have auto-committed with no <c>__EFMigrationsHistory</c> row
    /// to record them, so the next boot re-runs that migration from the top — fine for the
    /// <c>CREATE TABLE IF NOT EXISTS</c> shapes, not fine for a bare <c>ADD COLUMN</c>, and which one it
    /// is can only be settled by a human reading that migration.</para>
    /// </remarks>
    private static InvalidOperationException LeaseLost(
        SchemaReconcileLease lease, string migrationId, int statement, int statements, int completed)
        => new(
            $"The migration lease on \"{MigrationLeaseTable}\" (holder {lease.Holder}, fence {lease.Fence}) " +
            $"was lost while applying {migrationId}, so this process stopped rather than apply migrations " +
            $"concurrently with whoever holds it now. {completed} migration(s) were applied and recorded in " +
            $"\"__EFMigrationsHistory\" before this point, and " +
            (statement == 0
                ? $"no statement of {migrationId} was issued, so the next boot resumes cleanly from it."
                : $"{statement} of {statements} statement(s) of {migrationId} auto-committed with no history " +
                  $"row to record them, so the next boot will re-run {migrationId} from its first statement. " +
                  $"Check that those statements tolerate a re-run before restarting this pod."));
}
