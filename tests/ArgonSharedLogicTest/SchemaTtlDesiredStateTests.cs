namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;

/// <summary>
/// What the model says each table's rows expire on, read out of the real model.
/// </summary>
/// <remarks>
/// <para>The declaration this reads has never reached any database. <c>MultiregionalMigrationsSqlGenerator</c>
/// writes the row-level TTL clause from exactly one place — its <c>CreateTableOperation</c> override —
/// and EF produces no operation when an annotation changes on a table that already exists, which
/// <c>DbLocalityTests.Changing_a_locality_after_the_table_exists_produces_nothing</c> pins on purpose.
/// So the model is the only place the declaration exists, and reading it correctly is the whole
/// foundation both consumers stand on: <c>SchemaDeclarations</c> turns it into CockroachDB's
/// <c>ALTER</c>, <c>TtlSweepTargets</c> turns it into PostgreSQL's <c>DELETE</c>, and reading it wrong
/// points one or both of them at the wrong rows.</para>
///
/// <para>No database anywhere. The model is built against a connection string nothing dials — the same
/// technique <see cref="RegionTaggedIdTests"/> uses — because building a model opens nothing.</para>
/// </remarks>
[TestFixture]
public class SchemaTtlDesiredStateTests
{
    /// <summary>A host nothing dials. Building a model does not open a connection.</summary>
    private const string Unreachable = "Host=localhost;Database=schema-ttl-desired-state-tests";

    private static ApplicationDbContext Context()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
           .UseNpgsql(Unreachable)
           .Options;

        return new ApplicationDbContext(options, Options.Create(new DatabaseRegionOptions
        {
            PrimaryRegion   = "ru-central",
            ReplicateRegion = []
        }));
    }

    private static IReadOnlyDictionary<TableRef, TtlSettings> Desired()
    {
        using var context = Context();

        return SchemaTtlModel.ReadDesiredState(context.Model);
    }

    /// <summary>
    /// Exactly three tables carry a TTL, named as the database names them.
    /// </summary>
    /// <remarks>
    /// <para>Spelled out rather than derived, so that adding a fourth <c>WithTTL</c> — or deleting one
    /// — goes red with the table name in the message instead of quietly widening what a boot-time
    /// process is willing to issue <c>ALTER</c> against, and what an hourly sweep is willing to
    /// <c>DELETE</c> from.</para>
    ///
    /// <para>Two of these three names are the reason the reader keys on
    /// <c>IEntityType.GetTableName()</c>: neither <c>SpaceInvite</c> nor <c>DevTeamMemberInvite</c>
    /// calls <c>ToTable</c>, so their tables are the <c>DbSet</c> property names <c>Invites</c> and
    /// <c>TeamInvites</c> and match neither the CLR type nor anything a human would guess. A reader
    /// keyed on the type name would emit <c>ALTER TABLE "SpaceInvite"</c> and get "relation does not
    /// exist", which reads like a table that simply has not been created yet.</para>
    /// </remarks>
    [Test]
    public void The_three_tables_that_expire_rows_are_named_as_the_database_names_them()
        => Assert.That(Desired().Keys.Select(table => table.Name), Is.EquivalentTo(new[]
        {
            "Invites",
            "TeamInvites",
            "user_friend_requests"
        }));

    [Test]
    public void Every_declared_table_lives_in_the_public_schema()
        => Assert.That(Desired().Keys.Select(table => table.Schema), Is.All.EqualTo("public"));

    /// <summary>An invite expires on its own expiry column, daily, in tuned batches.</summary>
    [Test]
    public void Invites_expire_on_their_expiry_column()
    {
        var invites = Desired()[new TableRef("public", "Invites")];

        Assert.Multiple(() =>
        {
            Assert.That(invites.Enabled, Is.True);
            // Mixed case, unfolded: the generator delimits it, so the server keeps it, and the
            // canonical form on both sides is the column's own name.
            Assert.That(invites.ExpirationExpression, Is.EqualTo("ExpireAt"));
            Assert.That(invites.JobCron, Is.EqualTo("0 0 * * *"));
            Assert.That(invites.SelectBatchSize, Is.EqualTo(5000));
            Assert.That(invites.DeleteBatchSize, Is.EqualTo(5000));
            // Null, and that is the fix rather than an omission: the declaration used to carry
            // 52428800 — 50 MiB, copied from an attachment size limit — in a knob documented as rows
            // per second. Omitting it restores CockroachDB's own default of 100 rows/s instead of
            // switching the pacing off on the table the join path deletes from.
            Assert.That(invites.DeleteRateLimit, Is.Null);
        });
    }

    /// <summary>
    /// A TTL declared with every batch knob at zero has no opinion about batching.
    /// </summary>
    /// <remarks>
    /// This is the single normalisation most likely to be broken by someone tidying up, and the
    /// consequence is invisible until it is expensive. <c>WithTTL</c>'s defaults are <c>0</c>, and the
    /// generator skips a parameter whose value is zero — so a zero has never been written to a database
    /// and never should be. Emitting it as the number zero would tell CockroachDB to select nothing and
    /// delete nothing per batch: a working TTL switched off by a statement that looks like it switched
    /// one on.
    /// </remarks>
    [Test]
    public void A_batch_size_of_zero_is_no_opinion_rather_than_a_batch_size_of_zero()
    {
        var friendRequests = Desired()[new TableRef("public", "user_friend_requests")];

        Assert.Multiple(() =>
        {
            Assert.That(friendRequests.ExpirationExpression, Is.EqualTo("ExpiredAt"));
            Assert.That(friendRequests.SelectBatchSize, Is.Null);
            Assert.That(friendRequests.DeleteBatchSize, Is.Null);
            Assert.That(friendRequests.DeleteRateLimit, Is.Null);
        });
    }

    /// <summary>Nothing keys on a <c>DbSet</c> property or a CLR type, which are different strings here.</summary>
    [Test]
    public void No_table_is_keyed_by_the_name_of_the_entity_that_declares_it()
        => Assert.That(Desired().Keys.Select(table => table.Name)
               .Intersect(["SpaceInvite", "DevTeamMemberInvite", "FriendRequestEntity"]), Is.Empty);

    #region two entity types, one table

    private class Perishable
    {
        public Guid           Id       { get; set; }
        public DateTimeOffset ExpireAt { get; set; }
    }

    private sealed class Sturdy : Perishable { }

    /// <summary>
    /// One model per configuration, because EF caches the built model against the context type.
    /// </summary>
    /// <remarks>
    /// Without this every test here would be handed whichever model was built first and would assert
    /// against a configuration it did not write — the same trap <see cref="DbLocalityTests"/> documents.
    /// </remarks>
    private sealed class PerConfigurationModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context.GetType(), ((SharedTableContext)context).Configure, designTime);
    }

    private sealed class SharedTableContext(
        DbContextOptions<SharedTableContext> options,
        Action<ModelBuilder>                 configure) : DbContext(options)
    {
        public Action<ModelBuilder> Configure { get; } = configure;

        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder);
    }

    private static IReadOnlyDictionary<TableRef, TtlSettings> DesiredFor(Action<ModelBuilder> configure)
    {
        var options = new DbContextOptionsBuilder<SharedTableContext>()
           .UseNpgsql(Unreachable);

        options.ReplaceService<IModelCacheKeyFactory, PerConfigurationModelCacheKeyFactory>();

        using var context = new SharedTableContext(options.Options, configure);

        return SchemaTtlModel.ReadDesiredState(context.Model);
    }

    /// <summary>Base and derived share one table, so the base's declaration covers both.</summary>
    private static void Hierarchy(ModelBuilder builder, Action<EntityTypeBuilder<Sturdy>>? derived = null)
    {
        builder.Entity<Perishable>().ToTable("perishables").HasKey(x => x.Id);
        builder.Entity<Perishable>().WithTTL(x => x.ExpireAt, CronValue.Daily);

        var sturdy = builder.Entity<Sturdy>();
        derived?.Invoke(sturdy);
    }

    /// <summary>Table-per-hierarchy is one table, and one table has one TTL.</summary>
    [Test]
    public void A_hierarchy_mapped_to_one_table_produces_one_declaration()
    {
        var desired = DesiredFor(builder => Hierarchy(builder));

        Assert.Multiple(() =>
        {
            Assert.That(desired, Has.Count.EqualTo(1));
            Assert.That(desired.Single().Key.Name, Is.EqualTo("perishables"));
        });
    }

    /// <summary>Two entity types on one table saying the same thing is agreement, not a conflict.</summary>
    [Test]
    public void A_derived_type_repeating_the_same_declaration_is_not_a_conflict()
    {
        var desired = DesiredFor(builder =>
            Hierarchy(builder, sturdy => sturdy.WithTTL(x => x.ExpireAt, CronValue.Daily)));

        Assert.That(desired, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Two entity types on one table declaring different TTLs stops the process, with both names.
    /// </summary>
    /// <remarks>
    /// <c>MultiregionalMigrationsSqlGenerator</c> resolves this with <c>GetEntityTypes().FirstOrDefault(…)</c>,
    /// which picks by model-build order. Copying that here would make <em>when rows get deleted</em>
    /// depend on the order entity configurations happened to be registered in, and it would do it
    /// silently. Throwing is the fix; naming both entity types is what makes the throw useful at four
    /// in the morning.
    /// </remarks>
    [Test]
    public void Two_entity_types_on_one_table_with_different_ttls_is_a_hard_error()
    {
        var conflict = Assert.Throws<InvalidOperationException>(() => DesiredFor(builder =>
            Hierarchy(builder, sturdy => sturdy.WithTTL(x => x.ExpireAt, CronValue.Weekly))));

        var message = conflict?.Message ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain(nameof(Perishable)));
            Assert.That(message, Does.Contain(nameof(Sturdy)));
            Assert.That(message, Does.Contain("perishables"));
        });
    }

    #endregion
}
