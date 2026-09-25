namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using Argon.Grains.Persistence.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Daily stats and levels across the line between the grain's hot copy and the database.
/// </summary>
/// <remarks>
/// <para><c>UserStatsGrain</c> and <c>UserLevelGrain</c> both keep what they count in grain storage and
/// write it to the database later — on a timer, on deactivation, on a day change, on a claim. The
/// counting itself is covered by <c>UserStatsAndLevelTests</c>. What is covered here is the handover:
/// a day the grain slept through, a flush that lands on a day already written, a cached copy that is
/// lost while the database kept the record, and a database record that is lost while the grain kept
/// the copy. Each of those used to be reachable only by waiting for midnight, a ten-minute timer or an
/// incident.</para>
///
/// <para>Activations are ended on purpose (<see cref="UserStateGrainSupport.DeactivateAndWaitAsync"/>)
/// and the store is seeded before an activation reads it, which is the only way to put a grain on
/// either side of a boundary without waiting for the clock that normally moves it there.</para>
/// </remarks>
[TestFixture]
public class UserProgressPersistenceTests : TestBase
{
    private const string StatsState = "user-stats-store";
    private const string LevelState = "user-level-store";

    /// <summary>More than every level up to the cap needs, whatever the curve is tuned to.</summary>
    private const int EnoughForTheCap = 1_000_000;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ── Daily stats ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A day the grain slept through is written to the database before today starts, and only if it
    /// had anything unwritten.
    /// </summary>
    /// <remarks>
    /// The stats grain resets on activation when the stored day is not today. The reset used to be
    /// the only branch a test could see, because the stored day was always today by the time a test
    /// looked; seeding yesterday is what puts the flush in front of it.
    /// </remarks>
    [TestCase(true, TestName = "{m}(unflushed)")]
    [TestCase(false, TestName = "{m}(already flushed)")]
    [CancelAfter(120_000)]
    public async Task A_day_the_grain_slept_through_is_settled_before_today_starts(bool unflushed, CancellationToken ct = default)
    {
        var user      = await CreateSessionAsync(ct);
        var grain     = GetGrainFactory().GetGrain<IUserStatsGrain>(user.UserId);
        var yesterday = Today.AddDays(-1);

        await UserStateGrainSupport.WriteStateAsync(StatsState, grain, new UserStatsGrainState
        {
            CurrentDate          = yesterday,
            TimeInVoiceSeconds   = 600,
            CallsMade            = 2,
            MessagesSent         = 7,
            XpEarnedToday        = 20,
            MessageXpEarnedToday = 0,
            IsDirty              = unflushed
        });

        var today = await user.Users.GetTodayStats(ct);

        await using var db = await NewDbAsync(ct);

        var written = await db.UserDailyStats.AsNoTracking()
           .FirstOrDefaultAsync(s => s.UserId == user.UserId && s.Date == yesterday, ct);

        var stored = await UserStateGrainSupport.ReadStateAsync<UserStatsGrainState>(StatsState, grain);

        Assert.Multiple(() =>
        {
            Assert.That(today.timeInVoice, Is.Zero, "yesterday's voice time was carried into today");
            Assert.That(today.callsMade, Is.Zero);
            Assert.That(today.messagesSent, Is.Zero);
            Assert.That(stored.CurrentDate, Is.EqualTo(Today), "the store still holds yesterday");
            Assert.That(stored.IsDirty, Is.False, "a fresh day starts with nothing to write");
        });

        if (unflushed)
        {
            Assert.That(written, Is.Not.Null, "a day with unwritten stats was reset without being written");
            Assert.Multiple(() =>
            {
                Assert.That(written!.TimeInVoiceSeconds, Is.EqualTo(600));
                Assert.That(written.CallsMade, Is.EqualTo(2));
                Assert.That(written.MessagesSent, Is.EqualTo(7));
                Assert.That(written.XpEarned, Is.EqualTo(20));
            });
        }
        else
        {
            Assert.That(written, Is.Null, "a day already written was written again on activation");
        }
    }

    /// <summary>
    /// A second flush on the same day updates the day's row rather than inserting another.
    /// </summary>
    /// <remarks>
    /// The periodic flush runs every few minutes all day, so every flush after the first is this
    /// one. The key is (user, date); an insert here would fail on it and lose the whole day's update.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_second_flush_on_the_same_day_updates_the_day(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var grain = GetGrainFactory().GetGrain<IUserStatsGrain>(user.UserId);

        await grain.IncrementCallsAndWaitAsync();
        await grain.IncrementMessagesAndWaitAsync();
        await grain.FlushToDatabaseAsync();

        var first = await DayRowAsync(user.UserId, ct);

        await grain.IncrementCallsAndWaitAsync();
        await grain.RecordVoiceTimeAndWaitAsync(180, Guid.NewGuid(), Guid.NewGuid());
        await grain.FlushToDatabaseAsync();

        // Nothing changed since: the flush has nothing to do and must not touch the row.
        await grain.FlushToDatabaseAsync();

        var second = await DayRowAsync(user.UserId, ct);
        var stored = await UserStateGrainSupport.ReadStateAsync<UserStatsGrainState>(StatsState, grain);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null, "the first flush wrote nothing");
            Assert.That(first!.CallsMade, Is.EqualTo(1));
            Assert.That(first.MessagesSent, Is.EqualTo(1));

            Assert.That(second, Is.Not.Null);
            Assert.That(second!.CallsMade, Is.EqualTo(2), "the second flush did not reach the day's row");
            Assert.That(second.MessagesSent, Is.EqualTo(1));
            Assert.That(second.TimeInVoiceSeconds, Is.EqualTo(180));
            Assert.That(second.XpEarned, Is.EqualTo(6), "three minutes of voice at two XP a minute");

            Assert.That(stored.IsDirty, Is.False, "a flushed day still claims to have unwritten stats");
            Assert.That(stored.LastFlush, Is.Not.EqualTo(default(DateTimeOffset)));
        });
    }

    /// <summary>
    /// Messages earn one XP per ten, and no more than the daily cap however many are sent.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Messages_earn_xp_every_tenth_message_up_to_the_daily_cap(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var grain = GetGrainFactory().GetGrain<IUserStatsGrain>(user.UserId);

        // Nine: not yet a tenth.
        for (var i = 0; i < 9; i++)
            await grain.IncrementMessagesAndWaitAsync();

        var beforeTheTenth = (await user.Users.GetMyLevel(ct)).totalXp;

        await grain.IncrementMessagesAndWaitAsync();

        var afterTheTenth = (await user.Users.GetMyLevel(ct)).totalXp;

        // Up to the five hundredth — the fiftieth XP — and ten past it, which would be the fifty-first.
        for (var i = 10; i < 510; i++)
            await grain.IncrementMessagesAndWaitAsync();

        var level = await user.Users.GetMyLevel(ct);
        var stats = await grain.GetTodayStatsAsync();

        await grain.FlushToDatabaseAsync();

        var day = await DayRowAsync(user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(beforeTheTenth, Is.Zero, "XP was awarded before the tenth message");
            Assert.That(afterTheTenth, Is.EqualTo(1), "the tenth message earned nothing");
            Assert.That(stats.messagesSent, Is.EqualTo(510));
            Assert.That(level.totalXp, Is.EqualTo(50), "messages earned past the daily cap of fifty");
            Assert.That(day?.XpEarned, Is.EqualTo(50), "the day's record disagrees with the level about the cap");
        });
    }

    // ── Levels ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing, a negative amount, or anything at all while a coin is waiting to be claimed, leaves
    /// the level exactly where it was.
    /// </summary>
    /// <remarks>
    /// The last one is the rule the coin depends on: a user sitting on level 100 has a claim open, and
    /// XP arriving meanwhile must not start the next cycle underneath it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Xp_is_not_awarded_for_nothing_or_while_a_coin_waits_to_be_claimed(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var grain = GetGrainFactory().GetGrain<IUserLevelGrain>(user.UserId);

        await grain.AwardXpAsync(0, XpSource.Event);
        await grain.AwardXpAsync(-25, XpSource.Event);

        var afterNothing = await user.Users.GetMyLevel(ct);

        await grain.AwardXpAsync(EnoughForTheCap, XpSource.Event);

        var atTheCap = await user.Users.GetMyLevel(ct);

        await grain.AwardXpAsync(500, XpSource.Voice);

        var whileWaiting = await user.Users.GetMyLevel(ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterNothing.totalXp, Is.Zero, "a zero or negative award moved the XP");
            Assert.That(afterNothing.currentLevel, Is.EqualTo(1));

            Assert.That(atTheCap.currentLevel, Is.EqualTo(100));
            Assert.That(atTheCap.readyToClaimCoin, Is.True);
            Assert.That(atTheCap.xpForNextLevel, Is.EqualTo(atTheCap.xpForCurrentLevel),
                "there is no next level past the cap to show progress towards");

            Assert.That(whileWaiting.totalXp, Is.EqualTo(atTheCap.totalXp),
                "XP kept accumulating on a level whose coin has not been claimed");
            Assert.That(whileWaiting.readyToClaimCoin, Is.True);
        });
    }

    /// <summary>
    /// A level whose cached copy was lost is read back from the database, not restarted at level 1.
    /// </summary>
    /// <remarks>
    /// Grain storage is a cache in front of the <c>UserLevels</c> table. When the cache loses a user's
    /// entry — an eviction, a flushed cache, a region failover — the next activation finds nothing
    /// initialised and must load the database's record; the alternative is a user who comes back to
    /// level 1 with no XP.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_level_whose_cached_copy_was_lost_is_restored_from_the_database(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var grain = GetGrainFactory().GetGrain<IUserLevelGrain>(user.UserId);

        await grain.AwardXpAsync(400, XpSource.Voice);

        var before = await user.Users.GetMyLevel(ct);

        Assert.That(before.currentLevel, Is.GreaterThan(1), "premise: enough XP to be past level 1");

        // Deactivation writes a dirty level to the database; the store records that it did.
        Assert.That(await UserStateGrainSupport.DeactivateAndWaitAsync(grain, TimeSpan.FromSeconds(30), ct), Is.True,
            "the level grain never deactivated");

        var persisted = await Poll(async () =>
            (await UserStateGrainSupport.ReadStateAsync<UserLevelGrainState>(LevelState, grain)).IsDirty is false, ct);

        Assert.That(persisted, Is.True, "the deactivation never wrote the level to the database");

        await UserStateGrainSupport.ClearStateAsync<UserLevelGrainState>(LevelState, grain);

        var after = await user.Users.GetMyLevel(ct);
        var row   = await LevelRowAsync(user.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(after.totalXp, Is.EqualTo(before.totalXp), "the XP did not come back from the database");
            Assert.That(after.currentLevel, Is.EqualTo(before.currentLevel), "the level restarted from scratch");
            Assert.That(row?.TotalXpAllTime, Is.EqualTo(400));
        });
    }

    /// <summary>
    /// A level record missing from the database is recreated from the grain's copy on the next write.
    /// </summary>
    /// <remarks>
    /// The record is created when the grain first loads, and every later write used to assume it was
    /// still there. A row removed underneath a live grain — a manual cleanup, a restored backup that
    /// predates the account — must come back with what the grain knows rather than be skipped.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_level_record_missing_from_the_database_is_recreated_on_the_next_write(CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var grain = GetGrainFactory().GetGrain<IUserLevelGrain>(user.UserId);

        await grain.AwardXpAsync(250, XpSource.Voice);

        Assert.That(await LevelRowAsync(user.UserId, ct), Is.Not.Null, "premise: the first activation creates the row");

        await using (var db = await NewDbAsync(ct))
            await db.UserLevels.Where(l => l.UserId == user.UserId).ExecuteDeleteAsync(ct);

        var level = await user.Users.GetMyLevel(ct);

        Assert.That(await UserStateGrainSupport.DeactivateAndWaitAsync(grain, TimeSpan.FromSeconds(30), ct), Is.True,
            "the level grain never deactivated");

        var recreated = await Poll(async () => await LevelRowAsync(user.UserId, ct) is not null, ct);
        var row       = await LevelRowAsync(user.UserId, ct);

        Assert.That(recreated, Is.True, "the deactivation found no row and wrote nothing");
        Assert.Multiple(() =>
        {
            Assert.That(row!.TotalXpAllTime, Is.EqualTo(250));
            Assert.That(row.CurrentCycleXp, Is.EqualTo(level.totalXp));
            Assert.That(row.CurrentLevel, Is.EqualTo(level.currentLevel));
            Assert.That(row.CanClaimMedal, Is.False);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    private async Task<UserDailyStatsEntity?> DayRowAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        var today = Today;

        return await db.UserDailyStats.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId && s.Date == today, ct);
    }

    private async Task<UserLevelEntity?> LevelRowAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        return await db.UserLevels.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, ct);
    }

    private static async Task<bool> Poll(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;

            await Task.Delay(50, ct);
        }

        return await condition();
    }
}
