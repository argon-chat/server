namespace Argon.Features.Aegis;

using Argon.Core.Features.EF;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// The one table the identity server reaches the database for.
/// </summary>
/// <remarks>
/// <para>The data-protection keys are what encrypt the session cookie, and every replica of the role
/// has to hold the same ring or a user is signed out depending on which pod they land on. The
/// default store is a directory on the pod, which is exactly the wrong shape for that; the database
/// every replica already shares is the right one.</para>
///
/// <para>Its own context rather than <c>ApplicationDbContext</c> because the identity server keeps
/// that one off on purpose — see <c>AegisRole</c>: it is the role the whole internet reaches, and
/// what it cannot reach it cannot leak. This context maps <see cref="DataProtectionKeys"/> and
/// nothing else, so what the role can reach is exactly what it needs.</para>
///
/// <para>Self-contained the whole way down: its own migrations (<c>Migrations/KeyRing</c>), its own
/// history table and its own migration lease, so it shares nothing with the application schema but
/// the database. The two histories sit side by side because each is named; the two leases are
/// separate tables so a silo in the middle of a long migration does not hold up an identity server
/// that needs one table to exist, nor the other way round. <see cref="AegisKeyRingMigrations"/>
/// applies them at boot on every role that registers this context.</para>
///
/// <para>What it will not do is create the database. <c>CREATE DATABASE</c> is where the region
/// settings are decided, and a plain one issued from here would decide them before any silo had a
/// say — see <c>WarmUpExtension.CreateDatabaseAsync</c> for what that statement carries. A brand-new
/// cluster whose identity server boots ahead of every silo stops with that said out loud, and
/// starts normally once a silo has been through.</para>
/// </remarks>
public sealed class AegisKeyRingDbContext(DbContextOptions<AegisKeyRingDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public const string TableName = "DataProtectionKeys";

    /// <summary>
    /// Not <c>__EFMigrationsHistory</c>: that one is the application schema's, in the same database,
    /// and a second context writing rows into it would make each pipeline see the other's migrations
    /// as applied.
    /// </summary>
    public const string MigrationsHistoryTable = "__AegisKeyRingMigrationsHistory";

    /// <summary>Not <c>__MigrationLease</c> either, for the reason in the remarks.</summary>
    public const string MigrationLeaseTable = "__AegisKeyRingMigrationLease";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// How the context is wired wherever it is wired. The feature, the design-time scaffold and the
    /// tests all come through here, so the history table cannot be named differently in two places.
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string? connectionString)
        => options
           .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(MigrationsHistoryTable);

                // The same retry set as the application context: 40001 is CockroachDB asking for a
                // retry after a serialization conflict, which a rotation racing across replicas can
                // produce.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(2),
                    errorCodesToAdd: ["40001"]);
            })
            // CockroachDB has no advisory locks, which is what the stock repository takes around a
            // migration. The lease in AegisKeyRingMigrations is what serialises replicas instead.
           .ReplaceService<IHistoryRepository, NoLockHistoryRepository>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var key = modelBuilder.Entity<DataProtectionKey>();

        key.ToTable(TableName);

        // Spelled out rather than left to the provider, so the migration says what the columns are
        // instead of what a version of Npgsql happened to pick.
        key.Property(x => x.FriendlyName).HasColumnType("text");
        key.Property(x => x.Xml).HasColumnType("text");
    }
}
