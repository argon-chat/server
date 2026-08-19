namespace ArgonSharedLogicTest;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Operations;

/// <summary>
/// The DDL that decides where every row physically lives.
/// </summary>
/// <remarks>
/// <para>It runs once, when a database is created, and after that the only way to see whether it was
/// right is to watch latency in another country. So it is asserted here instead: the generator is
/// fed a model and its output is read, with no database anywhere near it.</para>
///
/// <para>The whole locality layer has been written and unused since it was added — exactly one entity
/// referenced it and the call was commented out — which is the state a thing reaches when nothing
/// checks it.</para>
/// </remarks>
[TestFixture]
public class DbLocalityTests
{
    private sealed class Widget
    {
        public Guid   Id   { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class LocalityContext(
        DbContextOptions<LocalityContext> options,
        Action<ModelBuilder>              configure) : DbContext(options)
    {
        /// <summary>The configuration this context's model is built from.</summary>
        public Action<ModelBuilder> Configure { get; } = configure;

        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder);
    }

    /// <summary>
    /// One model per configuration.
    /// </summary>
    /// <remarks>
    /// EF caches the built model against the context <em>type</em>, so without this every test here
    /// would be handed whichever model was built first and would assert against a table it did not
    /// configure — found by these tests disagreeing with each other depending on which won the race.
    /// The key is the configuration delegate because that is the thing that actually makes two models
    /// different; a field that exists only to be unique would say the same and explain nothing.
    /// </remarks>
    private sealed class PerConfigurationModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context.GetType(), ((LocalityContext)context).Configure, designTime);
    }

    private static LocalityContext Context(Action<ModelBuilder> configure)
    {
        var options = new DbContextOptionsBuilder<LocalityContext>()
           .UseNpgsql("Host=localhost;Database=locality-tests");

        // Called for their effect rather than chained: both are declared on the non-generic builder
        // and would lose the type argument the context constructor wants.
        options.UseMultiregionalCompatibility();
        options.ReplaceService<IModelCacheKeyFactory, PerConfigurationModelCacheKeyFactory>();

        return new LocalityContext(options.Options, configure);
    }

    /// <summary>The SQL a fresh database would be created with.</summary>
    private static string CreationSql(Action<ModelBuilder> configure)
    {
        using var context = Context(configure);

        var differ    = context.GetService<IMigrationsModelDiffer>();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var operations = differ.GetDifferences(null, context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        return string.Join("\n", generator.Generate(operations, context.Model).Select(c => c.CommandText));
    }

    private static void Widgets(ModelBuilder b) => b.Entity<Widget>().ToTable("widgets").HasKey(w => w.Id);

    [Test]
    public void A_table_with_no_locality_gets_no_locality_clause()
        => Assert.That(CreationSql(Widgets), Does.Not.Contain("LOCALITY"));

    /// <summary>
    /// The one for profiles, space metadata and archetypes: read from every region, written rarely.
    /// </summary>
    [Test]
    public void Global_places_a_table_everywhere()
    {
        var sql = CreationSql(b =>
        {
            Widgets(b);
            b.Entity<Widget>().PlacementGlobal();
        });

        Assert.That(sql, Does.Contain("LOCALITY GLOBAL"));
    }

    /// <summary>
    /// The one for messages: each row homed where it was written.
    /// </summary>
    /// <remarks>
    /// Nothing has to carry a region column for that to work. Cockroach defaults the hidden
    /// <c>crdb_region</c> to <c>gateway_region()</c>, so a row lands in the region that inserted it —
    /// and a channel's messages are only ever inserted by the activation that owns the channel, which
    /// lives in the space's home region. The pinning falls out of where the grain runs.
    /// </remarks>
    [Test]
    public void Regional_by_row_homes_each_row_where_it_was_written()
    {
        var sql = CreationSql(b =>
        {
            Widgets(b);
            b.Entity<Widget>().PlacementRegionalByRow();
        });

        Assert.That(sql, Does.Contain("LOCALITY REGIONAL BY ROW"));
    }

    [Test]
    public void Regional_by_table_can_name_a_region()
    {
        var sql = CreationSql(b =>
        {
            Widgets(b);
            b.Entity<Widget>().PlacementRegional("eu-central");
        });

        Assert.That(sql, Does.Contain("REGIONAL BY TABLE IN \"eu-central\""));
    }

    /// <summary>
    /// The database itself: which regions it spans and how much failure it is meant to survive.
    /// </summary>
    /// <remarks>
    /// Generated from a <c>CREATE DATABASE</c> operation rather than from the model diff, because
    /// that operation is what <c>EnsureCreated</c> and the first migration emit and it is the only
    /// place these clauses appear.
    /// </remarks>
    private static string DatabaseSql(string primary, string[] regions, string? survive = null)
    {
        using var context = Context(b =>
        {
            b.UseMultiRegionDatabase(primary, regions, survive);
            Widgets(b);
        });

        var generator = context.GetService<IMigrationsSqlGenerator>();

        var operations = new MigrationOperation[]
        {
            new NpgsqlCreateDatabaseOperation { Name = "argon" }
        };

        return string.Join("\n", generator.Generate(operations, context.Model).Select(c => c.CommandText));
    }

    /// <summary>
    /// One region cannot survive losing a region, and saying it must is not a wish — it is a
    /// database that will not create.
    /// </summary>
    /// <remarks>
    /// This is why the clause was written and then commented out: the constant it defaulted to was
    /// <c>REGION FAILURE</c>, and Cockroach needs three regions to place the replicas that would take
    /// over. So the goal is derived from how many regions there actually are.
    /// </remarks>
    [Test]
    public void One_region_survives_a_zone_failure()
    {
        var sql = DatabaseSql("ru-central", ["ru-central"]);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("PRIMARY REGION \"ru-central\""));
            Assert.That(sql, Does.Contain("SURVIVE ZONE FAILURE"));
        });
    }

    [Test]
    public void Three_regions_survive_a_region_failure()
    {
        var sql = DatabaseSql("ru-central", ["ru-central", "eu-central", "us-east"]);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("REGIONS \"ru-central\", \"eu-central\", \"us-east\""));
            Assert.That(sql, Does.Contain("SURVIVE REGION FAILURE"));
        });
    }

    /// <summary>Two is enough to fail over between and not enough for Cockroach to survive one.</summary>
    [Test]
    public void Two_regions_still_only_survive_a_zone_failure()
        => Assert.That(DatabaseSql("ru-central", ["ru-central", "eu-central"]),
            Does.Contain("SURVIVE ZONE FAILURE"));

    [Test]
    public void A_caller_that_knows_better_is_obeyed()
        => Assert.That(DatabaseSql("ru-central", ["ru-central"], "REGION FAILURE"),
            Does.Contain("SURVIVE REGION FAILURE"));

    /// <summary>
    /// Changing a table's locality later produces no migration, and that has to be known.
    /// </summary>
    /// <remarks>
    /// <para>The generator only writes <c>LOCALITY</c> as part of <c>CREATE TABLE</c>. Adding
    /// <c>PlacementGlobal()</c> to an entity whose table already exists therefore does nothing at all
    /// — no migration operation, no DDL, no error, and no way to tell from the code that it did not
    /// take.</para>
    ///
    /// <para>Asserted rather than fixed because the fix is worse than the fact: emitting an
    /// <c>ALTER TABLE … SET LOCALITY</c> from a migration rewrites where every row lives, which for
    /// the messages table is a data move that should be run deliberately and watched, not slipped
    /// into a deployment. Changing a locality after the fact is a one-line piece of DDL run by hand;
    /// what this test guarantees is that nobody discovers that by finding the change had silently
    /// not happened.</para>
    /// </remarks>
    [Test]
    public void Changing_a_locality_after_the_table_exists_produces_nothing()
    {
        using var before = Context(Widgets);
        using var after  = Context(b =>
        {
            Widgets(b);
            b.Entity<Widget>().PlacementGlobal();
        });

        var operations = before.GetService<IMigrationsModelDiffer>().GetDifferences(
            before.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            after.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.That(operations, Is.Empty,
            "if this ever starts producing operations, the generator has to learn ALTER TABLE … SET LOCALITY");
    }
}
