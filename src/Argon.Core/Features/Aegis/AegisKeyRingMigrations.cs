namespace Argon.Features.Aegis;

using System.Data.Common;
using Argon.Core.Features.EF;
using Argon.Features.Clustering;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// Brings the key ring's table up to date at boot, on whichever role registered the context.
/// </summary>
/// <remarks>
/// <para>The application schema's warm-up minus what does not apply. No <c>CREATE DATABASE</c> —
/// the remarks on <see cref="AegisKeyRingDbContext"/> say why; no PostgreSQL shims, because
/// nothing here references a CockroachDB built-in; no table declarations, because a ring is neither
/// placed nor expired. What is kept is the part that matters when several replicas boot at once:
/// the lease, and the statement-at-a-time application that renews it — shared with the
/// application schema's warm-up rather than copied, so the two cannot drift.</para>
///
/// <para>A held lease is waited out rather than skipped. The silos skip: whoever holds theirs is
/// applying the same migrations, and a silo boots fine against a schema somebody else is finishing.
/// This role does not — it cannot seal a cookie until the table exists — so it polls for the lease
/// up to a bound and, past that, boots with a warning rather than crash-looping behind a holder
/// that died with its lease unexpired. The key-ring health check is what then says whether the
/// table is there.</para>
/// </remarks>
public static class AegisKeyRingMigrations
{
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseWait     = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeasePoll     = TimeSpan.FromSeconds(2);

    extension(WebApplication app)
    {
        /// <summary>
        /// Applies the key ring's pending migrations, or returns at once on a role that has no ring.
        /// </summary>
        public async Task<WebApplication> WarmUpKeyRing()
        {
            using var scope = app.Services.CreateScope();

            // The registration is the switch: a role whose session feature did not run has no
            // context, and nothing to migrate.
            if (scope.ServiceProvider.GetService<AegisKeyRingDbContext>() is not { } db)
                return app;

            var role   = app.Services.GetRequiredService<RoleDescriptor>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<AegisKeyRingDbContext>>();

            await MigrateAsync(db, logger, role.Id.Value);

            return app;
        }
    }

    public async static Task MigrateAsync(
        AegisKeyRingDbContext db,
        ILogger<AegisKeyRingDbContext> logger,
        string roleId,
        CancellationToken ct = default)
    {
        var database = db.Database;

        if (!await database.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct))
            throw new InvalidOperationException(
                "The key ring's database does not exist yet, and the identity server does not create " +
                "databases: CREATE DATABASE is where the region settings are decided, and a plain one " +
                "issued from here would decide them before any silo had a say. Start a silo role first; " +
                "this process stops and will start normally once the database is there.");

        // One pinned session for the whole pass, for the reason WarmUpExtension gives: the tables
        // created here have to be visible to the very next statement, and the lease has to be renewed
        // on the session it protects. Released when the scoped context is disposed.
        await database.OpenConnectionAsync(ct);

        await using var lease = await AcquireAsync(database.GetDbConnection(), logger, roleId, ct);

        if (lease is null)
        {
            logger.LogWarning(
                "The key ring migration lease on {LockTable} stayed held for {Wait}; booting without " +
                "applying migrations. The key-ring health check will say whether the table is there",
                AegisKeyRingDbContext.MigrationLeaseTable, LeaseWait);

            return;
        }

        try
        {
            // Idempotent, so issued unconditionally rather than behind an ExistsAsync probe — the
            // application warm-up explains why the probe can disagree with the session that writes.
            await database.ExecuteSqlRawAsync(database.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), ct);

            var pending = (await database.GetPendingMigrationsAsync(ct)).ToList();

            if (pending.Count == 0)
            {
                logger.LogInformation("The key ring has no pending migrations");
                return;
            }

            await WarmUpExtension.ApplyMigrationsAsync(db, logger, lease, pending);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Applying the key ring's migrations failed");
            throw;
        }
    }

    private async static Task<SchemaReconcileLease?> AcquireAsync(
        DbConnection connection, ILogger logger, string roleId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LeaseWait;

        while (true)
        {
            var lease = await SchemaReconcileLease.TryAcquireAsync(
                connection, logger, roleId, LeaseLifetime, AegisKeyRingDbContext.MigrationLeaseTable, ct);

            if (lease is not null || DateTime.UtcNow >= deadline)
                return lease;

            await Task.Delay(LeasePoll, ct);
        }
    }
}
