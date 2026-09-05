namespace ArgonSharedLogicTest.Aegis;

using Argon.Core.Features.EF;
using Argon.Features.Aegis;
using Argon.Features.EF;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// The identity server's key ring: one table, one context, and a migration history of its own.
/// </summary>
/// <remarks>
/// <para>Model-only, like <c>TablePlacementAuditTests</c>: the context is built over a connection
/// string nothing dials, and its metadata, its migrations and its history repository are read.
/// Building a model opens no connection.</para>
///
/// <para>Everything pinned here fails silently otherwise. A model the migrations have fallen behind
/// is a table the ring cannot be written to, found on the first rotation after the change. A history
/// table shared with the application schema is two pipelines each treating the other's migrations
/// as applied, found when one of them re-runs something. A lease table shared with the silos is an
/// identity server waiting out a migration that has nothing to do with it.</para>
/// </remarks>
[TestFixture]
public class AegisKeyRingTests
{
    private const string Unreachable = "Host=localhost;Database=aegis-key-ring-tests";

    /// <summary>Through the same configuration the feature uses, so the test sees what the role sees.</summary>
    private static AegisKeyRingDbContext KeyRing()
    {
        var options = new DbContextOptionsBuilder<AegisKeyRingDbContext>();

        AegisKeyRingDbContext.Configure(options, Unreachable);

        return new AegisKeyRingDbContext(options.Options);
    }

    /// <summary>
    /// Anything more is a table the most exposed role in the product can now reach, and this is the
    /// line that notices.
    /// </summary>
    [Test]
    public void The_identity_server_maps_nothing_but_the_ring()
    {
        using var keyRing = KeyRing();

        Assert.Multiple(() =>
        {
            Assert.That(keyRing.Model.GetEntityTypes().Select(e => e.ClrType),
                Is.EquivalentTo(new[] { typeof(DataProtectionKey) }));

            Assert.That(keyRing.Model.FindEntityType(typeof(DataProtectionKey))?.GetTableName(),
                Is.EqualTo(AegisKeyRingDbContext.TableName));
        });
    }

    [Test]
    public void The_migrations_say_what_the_model_says()
    {
        using var keyRing = KeyRing();

        Assert.Multiple(() =>
        {
            Assert.That(keyRing.Database.GetMigrations(), Is.Not.Empty,
                "the key ring has no migrations at all, so nothing creates its table");

            Assert.That(keyRing.Database.HasPendingModelChanges(), Is.False,
                "the model changed and no migration followed; scaffold one with "
              + "--context AegisKeyRingDbContext --output-dir Migrations/KeyRing");
        });
    }

    [Test]
    public void The_history_and_the_lease_are_the_rings_own()
    {
        using var keyRing = KeyRing();

        var history = keyRing.GetService<IHistoryRepository>();

        Assert.Multiple(() =>
        {
            Assert.That(AegisKeyRingDbContext.MigrationsHistoryTable, Is.Not.EqualTo(HistoryRepository.DefaultTableName));

            Assert.That(history.GetCreateIfNotExistsScript(), Does.Contain(AegisKeyRingDbContext.MigrationsHistoryTable),
                "the name is declared but the repository was not told, so history rows would land in "
              + "the application schema's table");

            Assert.That(history, Is.InstanceOf<NoLockHistoryRepository>(),
                "the stock repository takes an advisory lock around a migration, which CockroachDB does not have");

            Assert.That(AegisKeyRingDbContext.MigrationLeaseTable,
                Is.Not.EqualTo(WarmUpExtension.MigrationLeaseTable)
                   .And.Not.EqualTo(SchemaReconcileLease.DefaultLockTable));
        });
    }
}
