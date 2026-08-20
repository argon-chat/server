namespace ArgonSharedLogicTest;

using Argon;
using Argon.Entities;
using Argon.Features.Clustering.Regions;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

/// <summary>
/// That an entity cannot reach the database with an identifier that has no region in it.
/// </summary>
/// <remarks>
/// <para>An untagged id is not a bug that gets fixed. It is a primary key, foreign keys point at it
/// within milliseconds of the insert, and <see cref="ArgonId.RegionIndexOf"/> has nothing to read —
/// so the row is homed in region zero for the rest of its life whether or not that is where it
/// belongs. The window in which this is repairable is the one before <c>SaveChanges</c>.</para>
///
/// <para>What used to guard it was that somebody remembered to write <c>Id = ArgonId.New()</c>, and
/// eight places did not. So the guard is a value generator on the model, and this asserts the thing
/// that generator is for: not that it is configured, but that an entity constructed with no id at
/// all comes out of EF's own generation path carrying a region. That is what fails on the day
/// somebody adds an entity whose key EF fills in for itself.</para>
///
/// <para>No database is involved anywhere here. The model is built from a connection string nothing
/// ever connects to, and <c>Add</c> is change tracking, not I/O.</para>
/// </remarks>
[TestFixture]
public class RegionTaggedIdTests
{
    /// <summary>An epoch far in the past, so identifiers are read as tagged rather than as legacy.</summary>
    /// <remarks>
    /// Which epoch is chosen does not decide whether <see cref="ArgonId.RegionIndexOf"/> answers at
    /// all — only a non-v7 makes it answer null — but reading against a real cutover is what these
    /// ids will be read against in production, so it is what they are read against here.
    /// </remarks>
    private static readonly DateTimeOffset Tagged = DateTimeOffset.UnixEpoch;

    /// <summary>A host nothing dials. Building a model does not open a connection.</summary>
    private const string Unreachable = "Host=localhost;Database=region-tagged-id-tests";

    private static ApplicationDbContext Context()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
           .UseNpgsql(Unreachable)
           .Options;

        // The regions the tables are placed in are irrelevant to value generation; the constructor
        // requires them, so it gets the shape a single-region deployment has.
        return new ApplicationDbContext(options, Options.Create(new DatabaseRegionOptions
        {
            PrimaryRegion   = "ru-central",
            ReplicateRegion = []
        }));
    }

    /// <summary>
    /// Every entity whose stored identity is a <see cref="Guid"/> Argon has to mint.
    /// </summary>
    /// <remarks>
    /// Derived here rather than asked of <c>ArgonIdGeneration</c>, deliberately. Reusing that
    /// predicate would make this test agree with the convention by construction and pass however
    /// narrow the convention became; the point is to state the requirement independently and let the
    /// two disagree out loud.
    /// </remarks>
    private static List<IEntityType> EntitiesKeyedByAGuid(IModel model)
        => model.GetEntityTypes()
           .Where(entityType => typeof(ArgonEntity).IsAssignableFrom(entityType.ClrType))
           .Where(HasASingleGuidKey)
           .OrderBy(entityType => entityType.ClrType.Name, StringComparer.Ordinal)
           .ToList();

    private static bool HasASingleGuidKey(IEntityType entityType)
    {
        var key = entityType.FindPrimaryKey();

        return key is { Properties.Count: 1 } && key.Properties[0].ClrType == typeof(Guid);
    }

    /// <summary>
    /// The whole point: an entity created without an id gets a region-bearing one anyway.
    /// </summary>
    /// <remarks>
    /// <para>Constructed uninitialised so that <c>Id</c> is <see cref="Guid.Empty"/> no matter what
    /// the record's constructor or field initialisers would have done — that is the state a caller
    /// who forgot leaves behind, and it is the only state in which EF generates at all.</para>
    ///
    /// <para><c>Add</c> rather than reaching for the generator directly, because what is being tested
    /// is the wiring and not the generator: whether EF <em>chooses</em> to generate for this property
    /// is decided by the key convention, by anything the entity configured on its own key, and by the
    /// annotation the convention writes. Calling <c>ArgonId.New()</c> here would assert nothing.</para>
    ///
    /// <para>Only that a region can be read, never which one. The stamp is process-global and other
    /// fixtures move it; the property that matters is that the identifier is a v7 carrying a tag,
    /// which holds for every region index.</para>
    /// </remarks>
    [Test]
    public void An_entity_saved_without_an_id_still_gets_a_region()
    {
        using var context = Context();

        var entityTypes = EntitiesKeyedByAGuid(context.Model);

        Assert.That(entityTypes, Has.Count.GreaterThan(20),
            "the model walk stopped finding entities, which would make every assertion below vacuously true");

        Assert.Multiple(() =>
        {
            foreach (var entityType in entityTypes)
            {
                var entity = RuntimeHelpers.GetUninitializedObject(entityType.ClrType);

                context.Add(entity);

                Assert.That(ArgonId.RegionIndexOf(((ArgonEntity)entity).Id, Tagged), Is.Not.Null,
                    $"{entityType.ClrType.Name} would be inserted with an id that names no region, "
                  + "and a primary key cannot be corrected afterwards");
            }
        });
    }

    /// <summary>
    /// An id the caller chose is the id that gets stored.
    /// </summary>
    /// <remarks>
    /// This is what makes the eighty-odd explicit <c>Id = ArgonId.New()</c> assignments redundancy
    /// rather than a conflict, and it is load-bearing beyond that: <see cref="ArgonId.NewIn"/> mints
    /// into a <em>sibling's</em> region, which is how a channel ends up where its space lives instead
    /// of where the request landed. A generator that overwrote an assigned id would silently re-home
    /// every one of those.
    /// </remarks>
    [Test]
    public void An_id_the_caller_assigned_is_left_alone()
    {
        using var context = Context();

        var chosen = ArgonId.Create(7);
        var space  = new SpaceEntity { Name = "assigned", Id = chosen };

        context.Add(space);

        Assert.That(space.Id, Is.EqualTo(chosen),
            "value generation must only fill in Guid.Empty, or ArgonId.NewIn stops meaning anything");
    }

    /// <summary>
    /// The value EF writes is the value that is stored, not a placeholder.
    /// </summary>
    /// <remarks>
    /// A temporary value would mean EF expects the database to hand back the real key on insert.
    /// There is no column default to hand one back, so the placeholder would be written and the
    /// change tracker would go on treating the key as unresolved.
    /// </remarks>
    [Test]
    public void A_generated_id_is_not_a_placeholder()
        => Assert.That(ArgonIdValueGenerator.Instance.GeneratesTemporaryValues, Is.False);

    /// <summary>An entity that exists only to be compared with and without the convention.</summary>
    private sealed record Sample : ArgonEntity;

    private sealed class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Sample>().ToTable("samples");
    }

    private sealed class TaggedContext(DbContextOptions<TaggedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sample>().ToTable("samples");
            modelBuilder.UseRegionTaggedIds();
        }
    }

    /// <summary>
    /// Turning this on does not change a single column, so no migration carries it.
    /// </summary>
    /// <remarks>
    /// <para>Client-side value generation is expressed as a core annotation, which never reaches the
    /// relational model — the column keeps the <c>ValueGeneratedOnAdd</c> the key convention gave it
    /// long before any of this existed, and the migrations differ sees nothing to write.</para>
    ///
    /// <para>Asserted rather than assumed because the deployment consequence is asymmetric. If it
    /// were wrong, every entity in the model would show up as a pending model change and the first
    /// person to scaffold a migration would get a diff touching sixty tables — discovered at the
    /// worst possible moment, and easy to "resolve" by generating it.</para>
    ///
    /// <para>Two context types rather than one configured two ways: EF caches a built model against
    /// the context type, so a single type would hand out whichever model was built first.</para>
    /// </remarks>
    [Test]
    public void Tagging_an_id_produces_no_migration()
    {
        using var plain  = new PlainContext(new DbContextOptionsBuilder<PlainContext>().UseNpgsql(Unreachable).Options);
        using var tagged = new TaggedContext(new DbContextOptionsBuilder<TaggedContext>().UseNpgsql(Unreachable).Options);

        var plainModel  = plain.GetService<IDesignTimeModel>().Model;
        var taggedModel = tagged.GetService<IDesignTimeModel>().Model;

        Assert.That(
            taggedModel.FindEntityType(typeof(Sample))!.FindPrimaryKey()!.Properties[0].GetValueGeneratorFactory(),
            Is.Not.Null,
            "the sample was not tagged at all, so comparing the two models proves nothing");

        var operations = plain.GetService<IMigrationsModelDiffer>()
           .GetDifferences(plainModel.GetRelationalModel(), taggedModel.GetRelationalModel());

        Assert.That(operations, Is.Empty,
            "region-tagged ids are client-side and must stay that way; the moment this produces DDL "
          + "it is a schema change to every table with a Guid key");
    }
}
