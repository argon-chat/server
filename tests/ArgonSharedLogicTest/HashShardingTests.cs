namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Options;

/// <summary>
/// <c>USING HASH</c> for monotonically increasing keys: what the generators write on each engine, and
/// what the migration that introduced it does.
/// </summary>
[TestFixture]
public class HashShardingTests
{
    private const string Unreachable = "Host=localhost;Database=hash-sharding-tests";

    private const string Migration = "20260922230415_HashShardingAndIndexAudit";

    private sealed class Widget
    {
        public Guid           Id        { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool           IsDeleted { get; set; }
    }

    private sealed class WidgetContext(DbContextOptions<WidgetContext> options, Action<ModelBuilder> configure) : DbContext(options)
    {
        public Action<ModelBuilder> Configure { get; } = configure;

        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder);
    }

    // EF caches the model per context type; without this every test would share the first one built.
    private sealed class PerConfigurationModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context.GetType(), ((WidgetContext)context).Configure, designTime);
    }

    private static WidgetContext Context(Action<ModelBuilder> configure, bool cockroach)
    {
        var options = new DbContextOptionsBuilder<WidgetContext>().UseNpgsql(Unreachable);

        options.UseArgonSchemaAnnotations();
        if (cockroach)
            options.UseMultiregionalCompatibility();
        options.ReplaceService<IModelCacheKeyFactory, PerConfigurationModelCacheKeyFactory>();

        return new WidgetContext(options.Options, configure);
    }

    private static ApplicationDbContext Argon(bool cockroach)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(Unreachable);

        options.UseArgonSchemaAnnotations();
        if (cockroach)
            options.UseMultiregionalCompatibility();

        return new ApplicationDbContext(options.Options, Options.Create(new DatabaseRegionOptions
        {
            PrimaryRegion   = "ru-central",
            ReplicateRegion = []
        }));
    }

    private static string Sql(DbContext context, IReadOnlyList<MigrationOperation> operations, IModel? model)
        => string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
           .Generate(operations, model)
           .Select(c => c.CommandText));

    private static string CreationSql(Action<ModelBuilder> configure, bool cockroach = true)
    {
        using var context = Context(configure, cockroach);

        var operations = context.GetService<IMigrationsModelDiffer>()
           .GetDifferences(null, context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        return Sql(context, operations, context.Model);
    }

    /// <summary>The operations a migration from <paramref name="before"/> to <paramref name="after"/> would carry, and the SQL for them.</summary>
    private static (IReadOnlyList<MigrationOperation> Operations, string Cockroach, string Postgres) Change(
        Action<ModelBuilder> before, Action<ModelBuilder> after)
    {
        using var from     = Context(before, cockroach: true);
        using var to       = Context(after, cockroach: true);
        using var postgres = Context(after, cockroach: false);

        var operations = from.GetService<IMigrationsModelDiffer>().GetDifferences(
            from.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            to.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        return (operations, Sql(to, operations, to.Model), Sql(postgres, operations, postgres.Model));
    }

    private static void Widgets(ModelBuilder b)
    {
        b.Entity<Widget>().ToTable("widgets").HasKey(w => w.Id);
        b.Entity<Widget>().HasIndex(w => w.CreatedAt).HasFilter("\"IsDeleted\" = false");
    }

    private static void PlainKey(ModelBuilder b) => b.Entity<Widget>().ToTable("widgets").HasKey(w => w.Id);

    private static void ShardedKey(ModelBuilder b)
    {
        PlainKey(b);
        b.Entity<Widget>().HasHashShardedKey();
    }

    private static void ShardedWidgets(ModelBuilder b)
    {
        b.Entity<Widget>().ToTable("widgets").HasKey(w => w.Id);
        b.Entity<Widget>().HasHashShardedKey();
        b.Entity<Widget>().HasIndex(w => w.CreatedAt).HasFilter("\"IsDeleted\" = false").IsHashSharded();
    }

    [Test]
    public void A_sharded_key_is_created_using_hash()
        => Assert.That(CreationSql(ShardedWidgets), Does.Contain("PRIMARY KEY (\"Id\") USING HASH"));

    [Test]
    public void A_sharded_index_says_so_between_its_columns_and_its_filter()
        => Assert.That(CreationSql(ShardedWidgets),
            Does.Contain("(\"CreatedAt\") USING HASH WHERE \"IsDeleted\" = false"));

    [Test]
    public void Nothing_is_sharded_unless_declared()
        => Assert.That(CreationSql(Widgets), Does.Not.Contain("USING HASH"));

    [Test]
    public void PostgreSQL_never_sees_the_clause()
        => Assert.That(CreationSql(ShardedWidgets, cockroach: false), Does.Not.Contain("USING HASH"));

    /// <summary>
    /// An existing key is re-sharded with <c>ALTER PRIMARY KEY</c>, never with a drop and re-add:
    /// Cockroach refuses to drop a primary key outside the transaction that adds its replacement, and
    /// PostgreSQL refuses while a foreign key depends on it.
    /// </summary>
    [Test]
    public void Sharding_an_existing_key_alters_it_in_place_on_cockroach_and_does_nothing_on_postgres()
    {
        var (operations, cockroach, postgres) = Change(PlainKey, ShardedKey);

        Assert.Multiple(() =>
        {
            Assert.That(operations.Select(o => o.GetType()), Is.EqualTo(new[] { typeof(AlterTableOperation) }));
            Assert.That(cockroach.Trim(), Is.EqualTo("ALTER TABLE widgets ALTER PRIMARY KEY USING COLUMNS (\"Id\") USING HASH;"));
            Assert.That(postgres.Trim(), Is.Empty);
        });
    }

    [Test]
    public void Unsharding_a_key_alters_it_back()
    {
        var (_, cockroach, _) = Change(ShardedKey, PlainKey);

        Assert.That(cockroach.Trim(), Is.EqualTo("ALTER TABLE widgets ALTER PRIMARY KEY USING COLUMNS (\"Id\");"));
    }

    [Test]
    public void Sharding_an_existing_index_rebuilds_it()
    {
        var (operations, cockroach, postgres) = Change(Widgets, b =>
        {
            Widgets(b);
            b.Entity<Widget>().HasIndex(w => w.CreatedAt).IsHashSharded();
        });

        Assert.Multiple(() =>
        {
            Assert.That(operations.Select(o => o.GetType()),
                Is.EqualTo(new[] { typeof(DropIndexOperation), typeof(CreateIndexOperation) }));
            Assert.That(cockroach, Does.Contain("(\"CreatedAt\") USING HASH"));
            Assert.That(postgres, Does.Not.Contain("USING HASH"));
        });
    }

    private static readonly string[] ShardedKeys =
    [
        "DeviceObservations", "FileBlobs", "FileCounters", "Files", "OperatorAuditLog", "Reports", "SystemNotifications"
    ];

    /// <summary>The audited set, exactly: a key added here is a table rewrite in production.</summary>
    [Test]
    public void Only_the_audited_insert_heavy_tables_have_sharded_keys()
    {
        using var context = Argon(cockroach: false);

        var sharded = context.Model.GetEntityTypes()
           .Where(e => e.IsHashShardedKey())
           .Select(e => e.GetTableName())
           .OrderBy(t => t, StringComparer.Ordinal);

        Assert.That(sharded, Is.EqualTo(ShardedKeys));
    }

    private static (string Cockroach, string Postgres) MigrationSql()
    {
        using var cockroach = Argon(cockroach: true);
        using var postgres  = Argon(cockroach: false);

        string Generate(ApplicationDbContext context)
        {
            var assembly  = context.GetService<IMigrationsAssembly>();
            var migration = assembly.CreateMigration(assembly.Migrations[Migration], context.Database.ProviderName!);
            var model     = context.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel!);

            return Sql(context, migration.UpOperations, model);
        }

        return (Generate(cockroach), Generate(postgres));
    }

    [Test]
    public void The_migration_shards_the_audited_keys_in_place_on_cockroach()
    {
        var (cockroach, _) = MigrationSql();

        Assert.Multiple(() =>
        {
            foreach (var table in ShardedKeys)
                Assert.That(cockroach, Does.Contain($"ALTER TABLE \"{table}\" ALTER PRIMARY KEY USING COLUMNS (\"Id\") USING HASH;"));

            Assert.That(cockroach, Does.Not.Contain("DROP CONSTRAINT"));
            Assert.That(cockroach, Does.Contain("CREATE INDEX \"IX_OperatorAuditLog_CreatedAt\" ON \"OperatorAuditLog\" (\"CreatedAt\") USING HASH;"));
        });
    }

    [Test]
    public void The_migration_is_plain_postgresql_on_postgresql()
    {
        var (_, postgres) = MigrationSql();

        Assert.Multiple(() =>
        {
            Assert.That(postgres, Does.Not.Contain("USING HASH"));
            Assert.That(postgres, Does.Not.Contain("ALTER PRIMARY KEY"));
            Assert.That(postgres, Does.Not.Contain("DROP CONSTRAINT"));
        });
    }

    /// <summary>
    /// The model and the last migration's snapshot agree, with the annotation provider production uses.
    /// </summary>
    /// <remarks>
    /// Without the provider the hash-sharding annotations never reach the relational model, and a
    /// migration scaffolded that way would silently unshard every table it touched.
    /// </remarks>
    [Test]
    public void The_snapshot_is_up_to_date()
    {
        using var context = Argon(cockroach: false);

        Assert.That(context.Database.HasPendingModelChanges(), Is.False,
            "the model has changes no migration carries; scaffold one (see AddMigration.ps1)");
    }
}
