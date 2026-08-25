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
/// <para><b>The criterion this fixture pins is frequency.</b> <c>LOCALITY GLOBAL</c> buys a local read
/// in every region and charges a commit-wait on every write — a few hundred milliseconds, set by the
/// cluster's maximum clock offset — so the question for each table is how many times one row is
/// written over its whole life against how often that row is read. A membership row inserted on join
/// and read by every permission check afterwards is the global shape. A row carrying a counter, or one
/// a background job rewrites, is not. The per-table counts live in <c>ArgonTablePlacement</c>, beside
/// each declaration, and it is worth reading them before changing a line here.</para>
///
/// <para><b>An earlier criterion asked "is it written by something a person just clicked", and this
/// fixture used to encode it.</b> That rule demoted six tables at once and separated nothing: creating
/// a space and editing a profile are clicks too, and both stayed global. It got <c>Channels</c>
/// outright wrong — a metadata row demoted for one column, <c>LastMessageId</c>, written once per
/// message — and the fix was to move the column out and put the table back. Four more came back on the
/// same reasoning applied properly: <c>UsersToServerRelations</c>, <c>MemberArchetypes</c>,
/// <c>ChannelEntitlementOverwrites</c> and <c>ChannelGroupEntity</c> hold rows written once or twice
/// ever and read by every permission evaluation in the product. Do not restore the old wording; it is
/// what produced the mistake.</para>
///
/// <para>The declarations were never applied to any database — the migrations predate them — which is
/// the only reason this was a correctable mistake rather than an incident. That also means nothing
/// downstream would notice a tenth <c>PlacementGlobal()</c> appearing: no migration, no DDL, no test,
/// right up until the reconciler is allowed to apply and moves the table's replicas. So the audit is
/// pinned here, as exact sets, by table name.</para>
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
    /// Nine tables are replicated to every region, and adding a tenth has to be an argument with a
    /// number in it.
    /// </summary>
    /// <remarks>
    /// <para>All nine are the shape the feature is documented for: a row written a handful of times
    /// over its whole life and read on every request that renders or authorises anything. An account
    /// and its profile, written per lifecycle. A space and a channel's metadata, written per
    /// lifecycle. The four rows a permission decision is made of — the membership, the roles it
    /// holds, the role definitions, and the per-channel overwrites — inserted on a join or by a
    /// moderator and then read by every evaluation. And the channel groups, created and reordered by
    /// a moderator and read by every bootstrap.</para>
    ///
    /// <para>Asserted as an exact set so that a tenth global cannot arrive unnoticed: nothing else in
    /// the build would fail, because the annotation produces no migration and no DDL until the
    /// reconciler applies it. The argument for adding one is the same as the argument that moved
    /// these — writes to one row over its life, reads of that row per day — and "the read side is
    /// attractive" on its own is what the first audit was already told.</para>
    /// </remarks>
    [Test]
    public void Only_the_audited_reference_tables_are_global()
        => Assert.That(TablesPlaced(Placement.Global), Is.EquivalentTo(new[]
        {
            "Archetypes", "ChannelEntitlementOverwrites", "ChannelGroupEntity", "Channels",
            "MemberArchetypes", "Spaces", "UserProfiles", "Users", "UsersToServerRelations"
        }));

    /// <summary>
    /// And the two tables with a hot column on an otherwise cold row stay homed in one region.
    /// </summary>
    /// <remarks>
    /// <para><c>ChannelLastMessages</c> exists because the column was moved rather than argued away:
    /// it is written once per flush per active channel, carrying every message since the last one,
    /// and it is the hottest write in the product after <c>Messages</c>. <c>Invites</c> is the same
    /// shape one step earlier — the row is minted once and never edited, except <c>UsedCount</c>,
    /// which is incremented per accepted join and is unbounded when <c>MaxUses</c> is zero. It also
    /// carries a row-level TTL, so a background job deletes from it in batches nobody asked for.</para>
    ///
    /// <para>The fix for <c>Invites</c> is the fix that already worked for <c>Channels</c>: move the
    /// counter to its own row and this table goes up. Changing this line without moving the counter
    /// is the mistake the first audit made in the other direction.</para>
    /// </remarks>
    [Test]
    public void The_tables_with_a_hot_column_stay_regional()
        => Assert.That(TablesPlaced(Placement.RegionalByTable),
            Is.EquivalentTo(new[] { "ChannelLastMessages", "Invites" }));

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

    /// <summary>
    /// The three tables a permission decision is read from share one placement, whatever it is.
    /// </summary>
    /// <remarks>
    /// <para>Every base-permission read is a single join across all three —
    /// <c>HybridPermissionCache.GetBasePermissionsAsync</c> walks <c>UsersToServerRelations</c> into
    /// <c>MemberArchetypes</c> into <c>Archetypes</c> to reach the entitlement mask, and
    /// <c>ArgonPermissionProvider.CanAccess</c> does the same with <c>Include</c>. A join is as far
    /// away as its furthest table, so splitting the three across two localities buys no local read
    /// for the global ones and pays the commit-wait anyway. That is precisely the arrangement the
    /// first audit left behind: <c>Archetypes</c> global, the other two regional, replication paid
    /// for and never usable.</para>
    ///
    /// <para>Stated as an invariant rather than as three assertions on one value, because the point
    /// survives a future decision to move all three down together. If someone demotes one of them
    /// this fails, and the fix is to move all three or none — not to relax this test.</para>
    /// </remarks>
    [Test]
    public void The_permission_join_is_not_split_across_localities()
    {
        var declarations = Declarations();

        var permissionTables = new[] { "UsersToServerRelations", "MemberArchetypes", "Archetypes" };

        foreach (var table in permissionTables)
            Assert.That(declarations.ContainsKey(table), Is.True,
                $"'{table}' declares no placement, and every base-permission read joins it");

        Assert.That(permissionTables.Select(table => declarations[table]).Distinct().Count(), Is.EqualTo(1),
            "the three tables a permission decision is read from must share one placement: "
          + string.Join(", ", permissionTables.Select(table => $"{table}={declarations[table]}")));
    }
}
