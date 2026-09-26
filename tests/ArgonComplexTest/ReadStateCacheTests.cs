namespace ArgonComplexTest;

using Argon.Core.Entities.Data;
using Argon.Core.Features.Logic;
using Argon.Entities;
using Argon.Services;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// What the read-state cache carries, and what happens to the entries a deploy inherits.
/// </summary>
/// <remarks>
/// <para>The cached value used to hold a message id and a mention count and nothing else, so a cache
/// hit rebuilt every entry with a null space while a miss read the real one off the row. The client
/// saw <c>spaceId</c> on the first badge fetch after a cold cache and nothing for the two hours that
/// entry lived — same user, same channel, two answers.</para>
///
/// <para>Fixing the encoding is the easy half. The half worth a test is the one a rollout hits: the
/// old two-field values do not disappear when the new build starts, they live out their TTL, and a
/// reader that treats a short value as malformed would take every user's badges down for two hours
/// on deploy day — a worse bug than the one being fixed.</para>
/// </remarks>
[TestFixture]
public class ReadStateCacheTests : TestBase
{
    private const int CacheDb = 6;

    private static string CacheKey(Guid userId) => ReadStateService.GetCacheKey(userId);

    private async Task WriteRawAsync(Guid userId, Guid channelId, string value)
    {
        var pool = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.Cache);

        await using var scope = pool.Rent();

        await scope.GetDatabase(CacheDb).HashSetAsync(CacheKey(userId), channelId.ToString(), value);
    }

    /// <summary>
    /// An entry written by the previous build is read, not rejected.
    /// </summary>
    /// <remarks>
    /// Written straight into Redis in the old shape rather than by running the old code, because the
    /// old code is gone — and this is the one case where the value under test is one the current build
    /// cannot produce. A space of null is the honest answer for an entry that never recorded one.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_value_from_the_previous_encoding_still_reads(CancellationToken ct = default)
    {
        var userId    = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        await WriteRawAsync(userId, channelId, "4200:3");

        var states = await FactoryAsp.Services
           .GetRequiredService<IReadStateService>()
           .GetAllReadStatesAsync(userId, ct);

        var entry = states.SingleOrDefault(state => state.ChannelId == channelId);

        Assert.That(entry, Is.Not.Null, "a two-field value read as malformed and the whole fetch was lost");

        Assert.Multiple(() =>
        {
            Assert.That(entry!.LastReadMessageId, Is.EqualTo(4200));
            Assert.That(entry.MentionCount, Is.EqualTo(3));
            Assert.That(entry.SpaceId, Is.Null, "an entry that never carried a space cannot invent one");
        });
    }

    /// <summary>
    /// And an entry written by this build carries the space back out again.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_value_from_the_current_encoding_carries_its_space(CancellationToken ct = default)
    {
        var userId    = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var spaceId   = Guid.NewGuid();

        await WriteRawAsync(userId, channelId, $"4200:3:{spaceId}");

        var states = await FactoryAsp.Services
           .GetRequiredService<IReadStateService>()
           .GetAllReadStatesAsync(userId, ct);

        var entry = states.SingleOrDefault(state => state.ChannelId == channelId);

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.SpaceId, Is.EqualTo(spaceId),
            "the space went into the cache and did not come back, which is the defect this encoding exists to fix");
    }

    // ── the writes behind the cache ─────────────────────────────────────────────────────────────

    private ReadStateService ReadStates
        => (ReadStateService)FactoryAsp.Services.GetRequiredService<IReadStateService>();

    private async Task<ChannelReadStateEntity?> RowAsync(Guid userId, Guid channelId, CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        return await db.ChannelReadStates
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.UserId == userId && x.ChannelId == channelId, ct);
    }

    /// <summary>
    /// An ack is one upsert: it moves the mark forward, clears the mentions, and takes the space from
    /// the channel when the caller did not name one. An ack behind the mark changes nothing.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task An_ack_only_moves_the_mark_forward(CancellationToken ct = default)
    {
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"acks-{Guid.NewGuid():N}"[..20], ct);

        var reader   = Guid.NewGuid();
        var newcomer = Guid.NewGuid();

        await ReadStates.IncrementMentionsAsync(reader, channelId, null, messageId: 90, delta: 2, ct: ct);
        await ReadStates.AckAsync(reader, channelId, null, 100, ct);
        var acked = await RowAsync(reader, channelId, ct);

        await ReadStates.IncrementMentionsAsync(reader, channelId, spaceId, messageId: 110, ct: ct);
        await ReadStates.AckAsync(reader, channelId, spaceId, 50, ct);
        var afterStale = await RowAsync(reader, channelId, ct);

        await ReadStates.AckAsync(newcomer, channelId, null, 7, ct);
        var inserted = await RowAsync(newcomer, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(acked!.LastReadMessageId, Is.EqualTo(100));
            Assert.That(acked.MentionCount, Is.Zero, "an ack left the mentions it read in place");
            Assert.That(acked.SpaceId, Is.EqualTo(spaceId), "an ack without a space did not take it from the channel");

            Assert.That(afterStale!.LastReadMessageId, Is.EqualTo(100), "an ack behind the mark moved it backwards");
            Assert.That(afterStale.MentionCount, Is.EqualTo(1), "an ack behind the mark cleared a mention it never read");

            Assert.That(inserted!.LastReadMessageId, Is.EqualTo(7));
            Assert.That(inserted.SpaceId, Is.EqualTo(spaceId));
        });
    }

    /// <summary>
    /// An ack after the cached hash was dropped must not leave one holding only its own channel, which
    /// the badge fetch would then serve as the whole list.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task An_ack_on_a_cold_cache_does_not_hide_the_other_channels(CancellationToken ct = default)
    {
        var userId = Guid.NewGuid();
        var first  = Guid.NewGuid();
        var second = Guid.NewGuid();

        await ReadStates.IncrementMentionsAsync(userId, first, null, messageId: 1, ct: ct);
        await ReadStates.IncrementMentionsAsync(userId, second, null, messageId: 1, delta: 2, ct: ct);
        Assert.That(await ReadStates.GetAllReadStatesAsync(userId, ct), Has.Count.EqualTo(2), "premise: both rows are read");

        await ReadStates.IncrementMentionsAsync(userId, second, null, messageId: 2, ct: ct);
        await ReadStates.AckAsync(userId, first, null, 10, ct);

        var states = await ReadStates.GetAllReadStatesAsync(userId, ct);

        Assert.That(states.Select(s => s.ChannelId), Is.EquivalentTo(new[] { first, second }),
            "an ack on a cold cache left only its own channel");
        Assert.That(states.Single(s => s.ChannelId == second).MentionCount, Is.EqualTo(3));
    }

    /// <summary>
    /// Mentions racing on one row are all counted. A read followed by a save lost increments here, and
    /// two first mentions at once collided on the insert.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Concurrent_mentions_are_all_counted(CancellationToken ct = default)
    {
        var userId    = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 16)
           .Select(_ => ReadStates.IncrementMentionsAsync(userId, channelId, null, messageId: 1, ct: ct)));

        Assert.That((await RowAsync(userId, channelId, ct))!.MentionCount, Is.EqualTo(16));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_batch_counts_each_user_once_and_creates_the_rows_it_lacks(CancellationToken ct = default)
    {
        var spaceId   = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var existing  = Guid.NewGuid();
        var fresh     = Guid.NewGuid();

        await ReadStates.IncrementMentionsAsync(existing, channelId, spaceId, messageId: 1, delta: 2, ct: ct);
        await ReadStates.BatchIncrementMentionsAsync(spaceId, channelId, 2, [existing, fresh, fresh], ct);

        var existingRow = await RowAsync(existing, channelId, ct);
        var freshRow    = await RowAsync(fresh, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(existingRow!.MentionCount, Is.EqualTo(3));
            Assert.That(freshRow!.MentionCount, Is.EqualTo(1), "a user named twice in one batch was counted twice");
            Assert.That(freshRow.LastReadMessageId, Is.Zero);
            Assert.That(freshRow.SpaceId, Is.EqualTo(spaceId));
        });
    }

    // A mention processed after the reader already acked past its message would sit on a read channel
    // with nothing left to clear it.
    [Test, CancelAfter(120_000)]
    public async Task A_mention_in_a_message_already_read_is_not_counted(CancellationToken ct = default)
    {
        var spaceId   = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var reader    = Guid.NewGuid();

        await ReadStates.AckAsync(reader, channelId, spaceId, 100, ct);

        await ReadStates.IncrementMentionsAsync(reader, channelId, spaceId, messageId: 90, ct: ct);
        await ReadStates.BatchIncrementMentionsAsync(spaceId, channelId, 100, [reader], ct);
        var afterRead = await RowAsync(reader, channelId, ct);

        await ReadStates.BatchIncrementMentionsAsync(spaceId, channelId, 101, [reader], ct);
        var afterNew = await RowAsync(reader, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterRead!.MentionCount, Is.Zero, "a mention in a read message was counted");
            Assert.That(afterNew!.MentionCount, Is.EqualTo(1));
        });
    }

    // After the newest messages are deleted, whoever had read everything else keeps no mention from them.
    [Test, CancelAfter(120_000)]
    public async Task Clearing_caught_up_mentions_leaves_readers_who_are_behind_alone(CancellationToken ct = default)
    {
        var spaceId   = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var caughtUp  = Guid.NewGuid();
        var behind    = Guid.NewGuid();

        await ReadStates.AckAsync(caughtUp, channelId, spaceId, 100, ct);
        await ReadStates.BatchIncrementMentionsAsync(spaceId, channelId, 150, [caughtUp, behind], ct);
        Assert.That((await ReadStates.GetAllReadStatesAsync(caughtUp, ct)).Single().MentionCount, Is.EqualTo(1),
            "premise: the cache holds the mention");

        await ReadStates.ClearCaughtUpMentionsAsync(channelId, 100, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await RowAsync(caughtUp, channelId, ct))!.MentionCount, Is.Zero);
            Assert.That((await ReadStates.GetAllReadStatesAsync(caughtUp, ct)).Single().MentionCount, Is.Zero,
                "the cached read state kept the cleared mention");
            Assert.That((await RowAsync(behind, channelId, ct))!.MentionCount, Is.EqualTo(1),
                "a reader with older messages still unread lost a mention");
        });
    }

    /// <summary>
    /// The @everyone bump for a large space walks the members in slices; whether the slice divides the
    /// roster evenly or not, every member is reached exactly once and the sender is skipped.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task An_everyone_bump_reaches_every_member_across_slices(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var created = await owner.Users.CreateSpace(new CreateServerRequest("Everyone", "slices", string.Empty), ct);
        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>(), $"{(created as FailedCreateSpace)?.error}");

        var spaceId = (created as SuccessCreateSpace)!.space.spaceId;
        var invite  = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();

        var members = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var member = await CreateSessionAsync(ct);
            Assert.That(await member.Users.JoinToSpace(invite, ct), Is.InstanceOf<SuccessJoin>());
            members.Add(member.UserId);
        }

        var channelId = Guid.NewGuid();

        await ReadStates.BumpEveryoneMentionsAsync(spaceId, channelId, owner.UserId, messageId: 1, rowsPerStatement: 2, ct);
        await ReadStates.BumpEveryoneMentionsAsync(spaceId, channelId, owner.UserId, messageId: 2, rowsPerStatement: 3, ct);

        var counts = new List<int?>();
        foreach (var member in members)
            counts.Add((await RowAsync(member, channelId, ct))?.MentionCount);

        var senderRow = await RowAsync(owner.UserId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(counts, Is.All.EqualTo(2), "a member was skipped or bumped twice by the slicing");
            Assert.That(senderRow, Is.Null, "the sender mentioned themselves");
        });
    }
}
