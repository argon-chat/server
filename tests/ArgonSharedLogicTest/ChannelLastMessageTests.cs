namespace ArgonSharedLogicTest;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.EF;
using Argon.Grains;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

/// <summary>
/// The counter that came off the channel row, and the rules that keep it off.
/// </summary>
/// <remarks>
/// <para><c>Channels.LastMessageId</c> was a hot counter on a cold row: written once per message
/// sent, on the row that carries a channel's name, description, group and ordering — the row every
/// client reads on bootstrap and every permission-adjacent path touches. Those two facts cannot both
/// be served by one placement, and the placement audit resolved the conflict by demoting the whole
/// table to regional. That treated the symptom. The counter now lives in <c>ChannelLastMessages</c>,
/// the channel row is metadata again, and <c>ArgonTablePlacement</c> has it back at
/// <c>PlacementGlobal</c> — see <see cref="TablePlacementAuditTests"/> for the set.</para>
///
/// <para>Which makes the placement conditional on something no compiler checks: that nothing writes
/// the channel row's counter any more. That is what this fixture is for. It is model-only and
/// source-only — no container, no database — because a guard that costs a container is a guard that
/// gets excluded from the run somebody actually does before pushing.</para>
/// </remarks>
[TestFixture]
public class ChannelLastMessageTests
{
    /// <summary>A host nothing dials. Building a model does not open a connection.</summary>
    private const string Unreachable = "Host=localhost;Database=channel-last-message-tests";

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

    /// <summary>
    /// The side table is four columns wide, and that is the whole design.
    /// </summary>
    /// <remarks>
    /// <para>The point of the split is that writing this row touches nothing anybody reads to render
    /// a channel. A fifth column is how that stops being true: a last author, a message count, a
    /// cached preview — each is a reason for a cold path to start reading a row written once per
    /// flush per active channel, and then the table is what <c>Channels</c> was, in a new place, and
    /// the next audit finds it. So the column set is pinned rather than described, and adding one is
    /// a decision somebody has to make on purpose.</para>
    ///
    /// <para>The key and the index are pinned for a duller reason: they are what the two readers'
    /// query plans depend on. <c>BadgeAggregationService</c> asks for every mark in a user's spaces
    /// and <c>SpaceReadGrain</c> for every mark in one — both by <c>SpaceId</c>, which is a sequential
    /// scan of the busiest table in the product if the index is not there, on the path that renders a
    /// client's first paint.</para>
    /// </remarks>
    [Test]
    public void The_side_table_carries_the_mark_and_nothing_else()
    {
        using var context = Context();

        var entity = context.Model.FindEntityType(typeof(ChannelLastMessageEntity));

        Assert.That(entity, Is.Not.Null, "the side table is not in the model at all");
        Assert.That(entity!.GetTableName(), Is.EqualTo("ChannelLastMessages"));

        Assert.Multiple(() =>
        {
            Assert.That(entity.GetProperties().Select(property => property.Name), Is.EquivalentTo(new[]
            {
                nameof(ChannelLastMessageEntity.ChannelId),
                nameof(ChannelLastMessageEntity.SpaceId),
                nameof(ChannelLastMessageEntity.LastMessageId),
                nameof(ChannelLastMessageEntity.UpdatedAt)
            }), "a column was added to the table whose entire purpose is not having any");

            Assert.That(entity.FindPrimaryKey()?.Properties.Select(property => property.Name),
                Is.EqualTo(new[] { nameof(ChannelLastMessageEntity.ChannelId) }),
                "one mark per channel, keyed by the channel — the upsert's ON CONFLICT names this");

            Assert.That(entity.GetIndexes().Any(index =>
                    index.Properties.Count == 1 &&
                    index.Properties[0].Name == nameof(ChannelLastMessageEntity.SpaceId)),
                Is.True,
                "both readers query by space; without this index they scan the table instead");
        });
    }

    /// <summary>
    /// And the column it replaced is still there, unwritten.
    /// </summary>
    /// <remarks>
    /// Dropping it is a second migration and a second decision, and keeping it is what makes this
    /// change reversible by redeploying the previous build. If it disappears without that decision
    /// being taken, the rollback stops existing quietly — which is exactly the kind of thing that is
    /// noticed at the moment it is needed and not before.
    /// </remarks>
    [Test]
    public void The_channel_row_still_carries_the_column_it_stopped_writing()
    {
        using var context = Context();

        var channel = context.Model.FindEntityType(typeof(ChannelEntity));

        Assert.That(channel?.FindProperty(nameof(ChannelEntity.LastMessageId)), Is.Not.Null,
            "Channels.LastMessageId is meant to stay mapped and unwritten until somebody decides to drop it");
    }

    /// <summary>
    /// Nothing writes the channel row's counter.
    /// </summary>
    /// <remarks>
    /// <para>This is the condition <c>Channels</c> being <c>LOCALITY GLOBAL</c> rests on, and nothing
    /// else in the build would notice it breaking: a write to that column compiles, passes every
    /// test, and simply starts charging a commit-wait of a few hundred milliseconds per message in
    /// every region once the placement reconciler is allowed to apply.</para>
    ///
    /// <para><b>What it looks for, and what it cannot see.</b> Two shapes: the EF bulk update the old
    /// writer used, and a raw <c>UPDATE</c> against the table. It cannot see a plain property
    /// assignment followed by <c>SaveChangesAsync</c>, because the same assignment syntax is how the
    /// grain fills the DTO field it hands back to a caller and how <c>ConversationEntity</c> — a
    /// different table with a same-named column — is written. The behavioural half of this guard is
    /// <c>ArgonComplexTest.ChannelBadgeTests</c>, which sends messages and asserts the column never
    /// moved; it catches every shape but needs a container.</para>
    ///
    /// <para>If this fires on a legitimate write to <c>ChannelLastMessages</c> — an EF bulk update
    /// replacing the upsert, say — the fix is to narrow the pattern, not to delete the test. What it
    /// must never be narrowed past is <c>Channels</c>.</para>
    /// </remarks>
    [Test]
    public void No_source_file_writes_the_channel_rows_counter()
    {
        // "SetProperty(c => c.LastMessageId" in any spelling of the lambda parameter. This is the
        // exact form ChannelGrain used before the counter moved.
        var bulkUpdate = new Regex(@"SetProperty\s*\(\s*(\w+)\s*=>\s*\1\s*\.\s*LastMessageId",
            RegexOptions.Compiled);

        // A hand-written UPDATE against the channel table. \W absorbs whatever quoting the literal
        // happened to use — "Channels" in a raw string, ""Channels"" in a verbatim one, \"Channels\"
        // in a plain one — because which literal syntax somebody reached for is not the point.
        var rawUpdate = new Regex(@"UPDATE\s+\W{0,3}Channels\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file.FullName);

            if (bulkUpdate.IsMatch(text))
                offenders.Add($"{file.FullName}: bulk-updates a LastMessageId column");

            if (rawUpdate.IsMatch(text))
                offenders.Add($"{file.FullName}: issues a raw UPDATE against \"Channels\"");
        }

        Assert.That(offenders, Is.Empty,
            "Channels is LOCALITY GLOBAL because nothing writes its counter any more; a writer here "
          + "puts a commit-wait back on the message path. Move the write to ChannelLastMessages, or "
          + "move the table back to PlacementRegional and say so in TablePlacementAuditTests.");
    }

    /// <summary>
    /// And the counter stays out of the space's channels token.
    /// </summary>
    /// <remarks>
    /// <para>The token gates the expensive branch of <c>GetSnapshot</c> — one grain call per visible
    /// channel — so a counter inside its hash meant every space with traffic re-sent its whole
    /// channel list every time the two-minute cache entry refilled, and the versioned bootstrap saved
    /// nothing in exactly the spaces it was built for. The answer stays correct either way, just
    /// expensive, which is why no test of the content notices.</para>
    ///
    /// <para>Asserted here, against <c>VersionOf</c> directly, because the end-to-end version in
    /// <c>ArgonComplexTest.ChannelHighWaterMarkTests</c> can no longer fail: the cached channel now
    /// carries the dead <c>Channels.LastMessageId</c>, which does not move, so hashing it would still
    /// produce a stable token. Two records differing in nothing but the counter is the statement that
    /// still means something.</para>
    /// </remarks>
    [Test]
    public void The_channels_token_ignores_the_high_water_mark()
    {
        var spaceId   = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        var quiet = new ArgonChannel(ChannelType.Text, spaceId, channelId, "general", null, null, "a0", 0, null);
        var busy  = quiet with { lastMessageId = 9_000 };

        Assert.That(CachedChannel.VersionOf([new CachedChannel(busy, [])]),
            Is.EqualTo(CachedChannel.VersionOf([new CachedChannel(quiet, [])])),
            "nobody created, renamed, moved or re-permissioned a channel — somebody only talked");
    }

    /// <summary>Every hand-written source file in the product, generated code excluded.</summary>
    /// <remarks>
    /// <c>Argon.CodeGen</c> and <c>Argon.CodeGenAdmin</c> are emitted from the <c>.ion</c> wire
    /// contracts and carry <c>lastMessageId</c> as a DTO field on purpose; migrations are a frozen
    /// record of what was already applied and are not editable history. Neither is a place a writer
    /// can be introduced, and both would make this fixture fail for reasons that are not the one it
    /// exists for.
    /// </remarks>
    private static IEnumerable<FileInfo> SourceFiles()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Argon.Server.slnx")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "could not find the repository root from the test directory");

        var source = new DirectoryInfo(Path.Combine(directory!.FullName, "src"));

        Assert.That(source.Exists, Is.True, $"nothing at '{source.FullName}'");

        return source
           .EnumerateFiles("*.cs", SearchOption.AllDirectories)
           .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                       && !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                       && !file.FullName.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                       && !file.FullName.Contains($"{Path.DirectorySeparatorChar}Argon.CodeGen{Path.DirectorySeparatorChar}")
                       && !file.FullName.Contains($"{Path.DirectorySeparatorChar}Argon.CodeGenAdmin{Path.DirectorySeparatorChar}"));
    }
}
