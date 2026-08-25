namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;

/// <summary>
/// What the boot-path declaration step reads, and exactly what SQL it would issue for it.
/// </summary>
/// <remarks>
/// <para>No database anywhere: a model is built over a connection string nothing dials, which is the
/// same technique <see cref="TablePlacementAuditTests"/> and <see cref="DbLocalityTests"/> use, because
/// building a model opens nothing. What the complex suite adds is whether the statements are accepted
/// by a real CockroachDB and reach the catalogue; this fixture is about whether they are the right
/// strings in the first place.</para>
///
/// <para>Two of the four things asserted here are guards rather than features — that
/// <c>REGIONAL BY ROW</c> renders no statement, and that two entity types disagreeing about one table
/// stops with both names — and both are worth a test precisely because nothing else would notice if
/// they stopped working. A deleted <c>REGIONAL BY ROW</c> guard converts the largest table in the
/// product on the next boot.</para>
/// </remarks>
[TestFixture]
public class SchemaDeclarationTests
{
    /// <summary>A host nothing dials. Building a model does not open a connection.</summary>
    private const string Unreachable = "Host=localhost;Database=schema-declaration-tests";

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

    private static IReadOnlyDictionary<TableRef, string> Localities()
    {
        using var context = Context();

        return SchemaDeclarations.ReadLocalities(context.Model);
    }

    #region reading the declaration

    /// <summary>
    /// Placement is keyed by the table the <c>ALTER</c> will name, not by the type that declared it.
    /// </summary>
    /// <remarks>
    /// The two are different strings in this model and that is the whole point: <c>SpaceMemberEntity</c>
    /// maps to <c>UsersToServerRelations</c> and <c>SpaceInvite</c> to <c>Invites</c>, so a step keyed
    /// on the CLR name would issue <c>ALTER TABLE "SpaceMemberEntity"</c> and get a relation that does
    /// not exist. <see cref="TablePlacementAuditTests"/> pins <em>which</em> tables are placed; this
    /// pins only that they are named the way the database names them.
    /// </remarks>
    [Test]
    public void Placements_are_keyed_by_table_name_and_not_by_entity_name()
    {
        var tables = Localities().Keys.Select(table => table.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(tables, Does.Contain("UsersToServerRelations"));
            Assert.That(tables, Does.Contain("Invites"));
            Assert.That(tables.Intersect(["SpaceMemberEntity", "SpaceInvite"]), Is.Empty);
        });
    }

    [Test]
    public void Every_placed_table_lives_in_the_public_schema()
        => Assert.That(Localities().Keys.Select(table => table.Schema), Is.All.EqualTo("public"));

    /// <summary>The three clause shapes the model actually declares, each read back verbatim.</summary>
    /// <remarks>
    /// Verbatim because the annotation is a raw SQL fragment that goes into the statement unchanged.
    /// If it ever stops being one — a structured value was considered — this is the test that says so
    /// rather than a database quietly acquiring a locality nobody can read.
    /// </remarks>
    [Test]
    public void The_model_declares_global_regional_and_regional_by_row()
    {
        var localities = Localities();

        Assert.Multiple(() =>
        {
            Assert.That(localities[new TableRef("public", "Users")], Is.EqualTo("GLOBAL"));
            Assert.That(localities[new TableRef("public", "Invites")], Is.EqualTo("REGIONAL BY TABLE"));
            Assert.That(localities[new TableRef("public", "Messages")], Is.EqualTo("REGIONAL BY ROW"));
        });
    }

    #endregion

    #region rendering the statement

    private static readonly TableRef Invites = new("public", "Invites");

    /// <summary>
    /// Identifiers are delimited, because Argon's are mixed case.
    /// </summary>
    /// <remarks>
    /// An unquoted <c>Invites</c> folds to <c>invites</c> and addresses a table that does not exist,
    /// which comes back as "relation does not exist" and reads like the table simply has not been
    /// created yet.
    /// </remarks>
    [Test]
    public void A_placement_statement_names_the_schema_and_the_table_quoted()
        => Assert.That(SchemaDeclarations.PlacementStatement(Invites, "GLOBAL"),
            Is.EqualTo("ALTER TABLE \"public\".\"Invites\" SET LOCALITY GLOBAL"));

    [Test]
    public void A_regional_by_table_placement_renders_its_clause_unchanged()
        => Assert.That(SchemaDeclarations.PlacementStatement(Invites, "REGIONAL BY TABLE"),
            Is.EqualTo("ALTER TABLE \"public\".\"Invites\" SET LOCALITY REGIONAL BY TABLE"));

    [Test]
    public void A_placement_pinned_to_one_region_carries_the_region_it_was_declared_with()
        => Assert.That(SchemaDeclarations.PlacementStatement(Invites, "REGIONAL BY TABLE IN \"eu-central\""),
            Is.EqualTo("ALTER TABLE \"public\".\"Invites\" SET LOCALITY REGIONAL BY TABLE IN \"eu-central\""));

    /// <summary>
    /// <c>REGIONAL BY ROW</c> renders nothing at all, in any spelling.
    /// </summary>
    /// <remarks>
    /// <para>This is the acceptance test for the one refusal in the step, and the reason it exists is
    /// worth restating where somebody about to delete it will read it: converting a populated table
    /// adds a hidden <c>crdb_region</c> column to the front of the primary key, repartitions the table
    /// and every secondary index, backfills all of it, and homes every existing row in the primary
    /// region — which for <c>Messages</c> is both the most expensive statement in the product and the
    /// wrong answer, since the rows should be homed where they were written.</para>
    ///
    /// <para>The <c>AS</c> form is here because it is the form the staged conversion will use, and it
    /// must be refused by the same line rather than slipping past a check written for the short one.</para>
    /// </remarks>
    [Test]
    public void Regional_by_row_renders_no_statement()
        => Assert.Multiple(() =>
        {
            Assert.That(SchemaDeclarations.PlacementStatement(Invites, "REGIONAL BY ROW"), Is.Null);
            Assert.That(SchemaDeclarations.PlacementStatement(Invites, "regional by row"), Is.Null);
            Assert.That(SchemaDeclarations.PlacementStatement(Invites, "REGIONAL  BY   ROW"), Is.Null);
            Assert.That(SchemaDeclarations.PlacementStatement(Invites, "REGIONAL BY ROW AS \"region\""), Is.Null);
        });

    /// <summary>
    /// The TTL statement: the expiration column delimited inside a string literal, and nothing the
    /// model has no opinion about.
    /// </summary>
    /// <remarks>
    /// The double wrapping is the part that breaks silently. <c>ttl_expiration_expression</c> takes a
    /// SQL <em>string</em> whose contents are a SQL expression, so the column has to be quoted inside
    /// the quotes: drop the inner pair and CockroachDB folds <c>ExpireAt</c> to <c>expireat</c> and
    /// refuses; drop the outer pair and it is a syntax error.
    /// </remarks>
    [Test]
    public void A_ttl_statement_quotes_the_expiration_column_inside_a_literal()
        => Assert.That(
            SchemaDeclarations.TtlStatement(Invites, TtlSettings.Declared("ExpireAt", "@daily", 5000, 5000, 0)),
            Is.EqualTo("ALTER TABLE \"public\".\"Invites\" SET (" +
                       "ttl_expiration_expression = '\"ExpireAt\"', " +
                       "ttl_job_cron = '0 0 * * *', " +
                       "ttl_select_batch_size = 5000, " +
                       "ttl_delete_batch_size = 5000)"));

    /// <summary>
    /// A batch knob left at zero is an absence of an opinion and is not written.
    /// </summary>
    /// <remarks>
    /// <c>WithTTL</c> defaults every knob to <c>0</c> and <c>FriendRequestEntity</c> takes all of those
    /// defaults. Writing a literal zero would tell the server to select nothing and delete nothing per
    /// batch, which is a working TTL turned off by a statement that looks like it turned it on.
    /// </remarks>
    [Test]
    public void A_ttl_statement_omits_the_knobs_the_model_leaves_at_zero()
        => Assert.That(
            SchemaDeclarations.TtlStatement(Invites, TtlSettings.Declared("ExpiredAt", null, 0, 0, 0)),
            Is.EqualTo("ALTER TABLE \"public\".\"Invites\" SET (" +
                       "ttl_expiration_expression = '\"ExpiredAt\"', " +
                       $"ttl_job_cron = '{TtlSettings.DefaultJobCron}')"));

    /// <summary>Every table the real model declares a TTL for renders a statement that can be read.</summary>
    /// <remarks>
    /// A shape check rather than three exact strings — those live in the assertions above — so that a
    /// fourth <c>WithTTL</c> is covered the day it is added rather than the day somebody remembers.
    /// </remarks>
    [Test]
    public void The_real_model_renders_a_ttl_statement_for_every_table_that_declares_one()
    {
        using var context = Context();

        var statements = SchemaTtlModel.ReadDesiredState(context.Model)
           .Select(pair => SchemaDeclarations.TtlStatement(pair.Key, pair.Value))
           .ToList();

        var malformed = statements.Where(sql =>
            !sql.StartsWith("ALTER TABLE \"public\".\"", StringComparison.Ordinal) ||
            !sql.Contains("ttl_expiration_expression = '\"", StringComparison.Ordinal) ||
            !sql.EndsWith(')')).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(statements, Is.Not.Empty);
            Assert.That(malformed, Is.Empty, string.Join(Environment.NewLine, malformed));
        });
    }

    #endregion

    #region two entity types, one table

    private class Widget
    {
        public Guid   Id   { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class HeavyWidget : Widget { }

    /// <summary>
    /// One model per configuration, because EF caches the built model against the context type.
    /// </summary>
    /// <remarks>
    /// Without this every test below would be handed whichever model was built first and would assert
    /// against a configuration it did not write — the trap <see cref="DbLocalityTests"/> documents.
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

    private static IReadOnlyDictionary<TableRef, string> LocalitiesFor(Action<ModelBuilder> configure)
    {
        var options = new DbContextOptionsBuilder<SharedTableContext>()
           .UseNpgsql(Unreachable);

        options.ReplaceService<IModelCacheKeyFactory, PerConfigurationModelCacheKeyFactory>();

        using var context = new SharedTableContext(options.Options, configure);

        return SchemaDeclarations.ReadLocalities(context.Model);
    }

    /// <summary>Base and derived share one table, so the base's declaration covers both.</summary>
    private static void Hierarchy(ModelBuilder builder, Action<EntityTypeBuilder<HeavyWidget>>? derived = null)
    {
        builder.Entity<Widget>().ToTable("widgets").HasKey(x => x.Id);
        builder.Entity<Widget>().PlacementGlobal();

        var heavy = builder.Entity<HeavyWidget>();
        derived?.Invoke(heavy);
    }

    [Test]
    public void A_hierarchy_mapped_to_one_table_produces_one_placement()
    {
        var placements = LocalitiesFor(builder => Hierarchy(builder));

        Assert.Multiple(() =>
        {
            Assert.That(placements, Has.Count.EqualTo(1));
            Assert.That(placements.Single().Key.Name, Is.EqualTo("widgets"));
        });
    }

    [Test]
    public void A_derived_type_repeating_the_same_placement_is_not_a_conflict()
        => Assert.That(
            LocalitiesFor(builder => Hierarchy(builder, heavy => heavy.PlacementGlobal())),
            Has.Count.EqualTo(1));

    /// <summary>
    /// Two entity types on one table declaring different placements stops, with both names.
    /// </summary>
    /// <remarks>
    /// <c>MultiregionalMigrationsSqlGenerator</c> resolves this with a <c>FirstOrDefault</c> over
    /// model-build order, and this model has TPH inheritance — so copying that would make where the
    /// data physically lives depend on the order entity configurations happened to be registered in,
    /// silently, and a <c>GLOBAL</c> table demoted that way pays a WAN read on every permission check
    /// with nothing to show for it. Naming both types is what makes the throw useful at four in the
    /// morning.
    /// </remarks>
    [Test]
    public void Two_entity_types_on_one_table_with_different_placements_is_a_hard_error()
    {
        var conflict = Assert.Throws<InvalidOperationException>(() => LocalitiesFor(builder =>
            Hierarchy(builder, heavy => heavy.PlacementRegional())));

        var message = conflict?.Message ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain(nameof(Widget)));
            Assert.That(message, Does.Contain(nameof(HeavyWidget)));
            Assert.That(message, Does.Contain("widgets"));
        });
    }

    #endregion
}
