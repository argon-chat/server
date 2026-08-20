namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;

/// <summary>
/// The placement audit itself: which tables are global, and — more to the point — which are not.
/// </summary>
/// <remarks>
/// <para>Ten tables were declared <c>LOCALITY GLOBAL</c> and six of them were wrong. Global buys a
/// local read in every region and charges a commit-wait on every write — a few hundred milliseconds,
/// set by the cluster's maximum clock offset — so it is a mode for reference data and nothing else.
/// Six of the ten were written on a user-facing click: a join, a role grant, a permission toggle, an
/// accepted invite, a channel rename, a drag-and-drop reorder. The reasoning per table is
/// <c>docs/architecture/table-placement-reconciler.md</c> §5b, and it is worth reading before
/// changing a line of this fixture, because the arguments that sound most convincing for moving a
/// table back to global are all read-side arguments and the read side was never the objection.</para>
///
/// <para>The declarations were never applied to any database — the migrations predate them — which is
/// the only reason this was a correctable mistake rather than an incident. That also means nothing
/// downstream would have noticed an eleventh <c>PlacementGlobal()</c> appearing: no migration, no
/// DDL, no test, right up until the reconciler is allowed to apply and moves the table's replicas.
/// So the audit is pinned here, as a set, by table name.</para>
///
/// <para>Model-only, like <c>DbLocalityTests</c>: a context is built over a connection string nothing
/// dials and its annotations are read. The complex suite's <c>TablePlacementTests</c> asks the other
/// half of the question — whether the declaration reached the server — and is red on purpose until
/// the reconciler runs.</para>
/// </remarks>
[TestFixture]
public class TablePlacementAuditTests
{
    /// <summary>A host nothing dials. Building a model does not open a connection.</summary>
    private const string Unreachable = "Host=localhost;Database=table-placement-audit-tests";

    private const string LocalityAnnotation = "Regional:Locality";

    private enum Placement
    {
        Global,
        RegionalByTable,
        RegionalByRow
    }

    private static ApplicationDbContext Context()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
           .UseNpgsql(Unreachable)
           .Options;

        // A single-region deployment, which is what the constructor needs and what production is.
        // The region set does not reach the per-table annotations at all — those are placements, not
        // region names — so nothing here depends on it.
        return new ApplicationDbContext(options, Options.Create(new DatabaseRegionOptions
        {
            PrimaryRegion   = "ru-central",
            ReplicateRegion = []
        }));
    }

    /// <summary>
    /// Which of the three placements a declaration means, however it happens to be spelled.
    /// </summary>
    /// <remarks>
    /// Read through the letters rather than compared against a literal, because the annotation's
    /// payload is on its way from a raw SQL fragment (<c>"REGIONAL BY TABLE"</c>) to a structured
    /// value, and this fixture pins the audit — which tables — rather than the encoding.
    /// <c>DbLocalityTests</c> already pins the exact clause the generator writes, which is the place
    /// a spelling change has to be noticed. What must not degrade is the refusal: an unrecognised
    /// declaration returns null and fails the run, because a table whose placement cannot be read is
    /// a table with no placement, and silently skipping it would let the audit shrink by one.
    /// </remarks>
    private static Placement? Classify(object declared)
    {
        var letters = new string((declared.ToString() ?? "").Where(char.IsLetter).ToArray()).ToUpperInvariant();

        if (letters.Contains("REGIONALBYROW"))
            return Placement.RegionalByRow;

        if (letters.Contains("REGIONALBYTABLE"))
            return Placement.RegionalByTable;

        if (letters.Contains("GLOBAL"))
            return Placement.Global;

        return null;
    }

    /// <summary>
    /// Every placement the production model declares, keyed the way the reconciler keys them.
    /// </summary>
    /// <remarks>
    /// <para>By table name, not by entity name, because the two differ in ways that matter:
    /// <c>SpaceMemberEntity</c> maps to <c>UsersToServerRelations</c>, and <c>ChannelGroupEntity</c>
    /// has neither a <c>DbSet</c> nor a <c>ToTable</c> so its table is literally the class name. A
    /// rename of any of them shows up here as a missing table with a name attached, which is the
    /// point — the reconciler would emit an <c>ALTER TABLE</c> against something that does not exist.</para>
    ///
    /// <para>Read from <c>context.Model</c> — the runtime model — on purpose: that is what the
    /// reconciler reads, and custom annotations are supposed to survive EF's runtime-model pruning
    /// because it only strips keys it owns. If this ever comes back empty, that assumption broke and
    /// the reconciler is reading nothing at all. Do not repair it by switching to the design-time
    /// model; that would make this fixture green and production silent.</para>
    /// </remarks>
    private static Dictionary<string, Placement> Declarations()
    {
        using var context = Context();

        var declarations = new Dictionary<string, Placement>(StringComparer.Ordinal);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.FindAnnotation(LocalityAnnotation)?.Value is not { } declared)
                continue;

            var table     = entityType.GetTableName();
            var placement = Classify(declared);

            Assert.That(table, Is.Not.Null,
                $"{entityType.ClrType.Name} declares a placement and maps to no table of its own");
            Assert.That(placement, Is.Not.Null,
                $"'{declared}' on {entityType.ClrType.Name} is not a placement this fixture can read");

            // Several entity types can map to one table — the model has TPH inheritance — and two of
            // them disagreeing would make physical data placement depend on the order entity types
            // were registered in. It is a modelling mistake rather than a policy choice, so it is
            // caught here rather than resolved.
            if (declarations.TryGetValue(table!, out var already))
                Assert.That(placement!.Value, Is.EqualTo(already),
                    $"'{table}' is declared twice with two different placements; one table, one decision");

            declarations[table!] = placement!.Value;
        }

        Assert.That(declarations, Is.Not.Empty,
            "no table declares a placement, so either PlaceArgonTables stopped being called or the "
          + "annotations did not survive into the runtime model — the reconciler reads this");

        return declarations;
    }

    private static string[] TablesPlaced(Placement placement)
        => Declarations()
           .Where(declaration => declaration.Value == placement)
           .Select(declaration => declaration.Key)
           .OrderBy(table => table, StringComparer.Ordinal)
           .ToArray();

    /// <summary>
    /// Four tables are replicated to every region, and adding a fifth has to be an argument.
    /// </summary>
    /// <remarks>
    /// These four are the shape the feature is documented for: an account, its profile, a space's
    /// metadata, and the archetypes every permission evaluation reads. Three of them are written once
    /// per lifecycle. The fourth, <c>Archetypes</c>, is the borderline one and survives on its read
    /// side alone — if role editing ever becomes interactive-frequency it goes regional with
    /// <c>MemberArchetypes</c>. Asserted as an exact set so that an eleventh global cannot arrive
    /// unnoticed: nothing else in the build would fail, because the annotation produces no migration
    /// and no DDL until the reconciler applies it.
    /// </remarks>
    [Test]
    public void Only_the_audited_reference_tables_are_global()
        => Assert.That(TablesPlaced(Placement.Global),
            Is.EquivalentTo(new[] { "Archetypes", "Spaces", "UserProfiles", "Users" }));

    /// <summary>
    /// And the six the audit moved off global stay homed in one region.
    /// </summary>
    /// <remarks>
    /// Every one of these is written on something a person just clicked — join and leave
    /// (<c>UsersToServerRelations</c>), role grant and revoke (<c>MemberArchetypes</c>), permission
    /// toggle (<c>ChannelEntitlementOverwrites</c>), accepted invite (<c>Invites</c>, whose UsedCount
    /// increment is a compare-and-swap that only works against one authoritative copy), channel
    /// create/rename/move (<c>Channels</c>), and drag-and-drop reorder (<c>ChannelGroupEntity</c>).
    /// If one of them turns up in the global set instead, this fails naming it, and the fix is to
    /// read §5b rather than to edit the expectation.
    /// </remarks>
    [Test]
    public void The_interactively_written_tables_are_regional()
        => Assert.That(TablesPlaced(Placement.RegionalByTable), Is.EquivalentTo(new[]
        {
            "ChannelEntitlementOverwrites", "ChannelGroupEntity", "Channels",
            "Invites", "MemberArchetypes", "UsersToServerRelations"
        }));

    /// <summary>
    /// Messages, and only Messages, are placed a row at a time.
    /// </summary>
    /// <remarks>
    /// Row-level placement is the expensive kind to declare: converting a populated table is an
    /// <c>ALTER PRIMARY KEY</c> that rewrites every index on it. A second table acquiring this by
    /// analogy — "messages are regional by row, so direct messages should be too" — is a schema
    /// change nobody costed, which is why the set is pinned rather than the one entry checked.
    /// </remarks>
    [Test]
    public void Only_messages_are_placed_row_by_row()
        => Assert.That(TablesPlaced(Placement.RegionalByRow), Is.EquivalentTo(new[] { "Messages" }));
}
