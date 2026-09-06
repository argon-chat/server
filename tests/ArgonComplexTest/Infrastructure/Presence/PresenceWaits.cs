namespace ArgonComplexTest.Infrastructure.Presence;

using Argon.Features.Logic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

/// <summary>
/// Every interval a presence test waits out, derived from the host's own presence configuration.
/// </summary>
/// <remarks>
/// <para>A presence assertion is almost always about a ratio rather than about a number of seconds:
/// "this key was renewed inside the last tick", "this is past the TTL cliff", "nothing happened for
/// longer than the debounce", "the grace has had a reminder tick to fire in". Written as literals
/// those ratios were invisible — a reader had to know that 35 was two fifteen-second ticks with five
/// seconds of slack, and that 90 was a hundred and twenty minus that — and they were unchangeable,
/// because changing one number meant re-deriving thirty others by hand.</para>
///
/// <para>Everything here is computed from <see cref="PresenceTimingOptions"/> as the running host
/// bound it, so the same expression means the same thing at production timings and at the
/// compressed ones the integration host uses (<see cref="TestPresenceTimings"/>). That is what makes
/// the speed-up honest: nothing is asserted more weakly, the same relationships are checked, and a
/// fixture would still pass against the shipped two-minute TTL — it would simply take twenty times
/// as long to say so.</para>
///
/// <para>The one value that is <em>not</em> derived is <see cref="Settle"/>. It is a budget for an
/// RPC, a Redis fold and a stream hop to complete, and that does not get faster because a TTL got
/// shorter, so scaling it with the presence clocks would buy flakiness and nothing else.</para>
/// </remarks>
public static class PresenceWaits
{
    /// <summary>The presence clocks the host under test is actually running on.</summary>
    /// <remarks>
    /// Read from the host's container rather than from <see cref="TestPresenceTimings"/>, so a
    /// setting that failed to reach configuration shows up as a fixture that behaves oddly at the
    /// shipped values rather than as one that silently asserts against numbers nothing is using.
    /// </remarks>
    public static PresenceTimingOptions Timings
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IOptions<PresenceTimingOptions>>().Value;

    /// <summary>
    /// Orleans' real floor on a reminder period, read back from the silo.
    /// </summary>
    /// <remarks>
    /// <see cref="PresenceTimingOptions.ReminderFloor"/> is a mirror of this and the two are set
    /// together by the test host; this is the original, and it is exposed so a fixture can prove the
    /// host's <c>Configure&lt;ReminderOptions&gt;</c> took effect instead of assuming it. If it had
    /// not, every grace-period test would silently wait a minute for a five-second grace.
    /// </remarks>
    public static TimeSpan OrleansReminderFloor
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IOptions<ReminderOptions>>().Value.MinimumReminderPeriod;

    /// <summary>
    /// The cushion added to any wait that must be strictly longer than a product interval.
    /// </summary>
    /// <remarks>
    /// Absolute rather than proportional, and deliberately so: what it covers is a scheduler quantum,
    /// a grain call and a Redis round trip on a loaded CI box, and none of those shrink when a TTL
    /// does. It is also the gap between <see cref="FreshTtlFloor"/> and
    /// <see cref="DrainedTtlCeiling"/>, which is what keeps "still being refreshed" and "no longer
    /// being refreshed" from ever being the same reading.
    /// </remarks>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>The session grain's refresh period, raw.</summary>
    public static TimeSpan Tick => Timings.RefreshPeriod;

    /// <summary>The session TTL, raw — how long a key lives with nothing renewing it.</summary>
    public static TimeSpan SessionTtl => Timings.SessionTtl;

    /// <summary>Long enough that at least one refresh tick has certainly run.</summary>
    public static TimeSpan OneTick => Ticks(1);

    /// <summary>Long enough that two have.</summary>
    /// <remarks>
    /// The standard quiet period for "is this session still renewing its keys": one tick could be
    /// missed to a scheduler hiccup, two is a timer that is not running.
    /// </remarks>
    public static TimeSpan TwoTicks => Ticks(2);

    /// <summary><paramref name="count"/> refresh ticks, plus the usual cushion.</summary>
    public static TimeSpan Ticks(int count) => Tick * count + Slack;

    /// <summary>Past the point where an unrenewed presence or status key has certainly lapsed.</summary>
    public static TimeSpan PastTtl => SessionTtl + Tick + Slack;

    /// <summary>The activity entry's lifetime, raw.</summary>
    public static TimeSpan ActivityTtl => Timings.ActivityTtl;

    /// <summary>Past the point where an unrenewed activity entry has certainly lapsed.</summary>
    public static TimeSpan PastActivityTtl => ActivityTtl + Tick + Slack;

    /// <summary>
    /// An activity TTL at or above this was re-armed within the last tick or two.
    /// </summary>
    /// <remarks>
    /// The session tick renews the activity alongside the status keys, so a live session's activity
    /// entry is never further below its full lifetime than the tick that renews it — two of them,
    /// for the same reason <see cref="FreshTtlFloor"/> allows two.
    /// </remarks>
    public static TimeSpan FreshActivityTtlFloor => ActivityTtl - Tick * 2;

    /// <summary>
    /// Long enough for an armed grace reminder to have fired at least once.
    /// </summary>
    /// <remarks>
    /// The grace is a periodic reminder, and Orleans will not schedule one below its own floor, so
    /// the worst case for "the grace has had its chance" is the period plus one more of those.
    /// </remarks>
    public static TimeSpan GraceAndABit => Timings.GracePeriod + Timings.ReminderFloor + Slack;

    /// <summary>
    /// The whole ungraceful-drop path: the presence key lapsing, then the grace noticing.
    /// </summary>
    /// <remarks>
    /// The budget for "a device that vanished is eventually finalized offline". The reminder only
    /// finalizes once the presence key has gone, so the two waits are sequential rather than
    /// concurrent, and a test that allowed only the grace would be racing the TTL.
    /// </remarks>
    public static TimeSpan OfflineDeadline => SessionTtl + GraceAndABit;

    /// <summary>
    /// Comfortably inside the disconnect grace: where a reconnect the grace is meant to make
    /// invisible belongs.
    /// </summary>
    public static TimeSpan InsideGrace => Timings.GracePeriod / 2;

    /// <summary>Long enough for the statusless deadline to have assumed Online.</summary>
    public static TimeSpan StatusDeadlineAndABit => Timings.StatusDeadline + Slack;

    /// <summary>Long enough that the next heartbeat is past the debounce and will reach Redis.</summary>
    public static TimeSpan PastHeartbeatDebounce => Timings.HeartbeatDebounce + Slack;

    /// <summary>
    /// A TTL at or above this was renewed within the last tick or two — the key is being kept alive.
    /// </summary>
    /// <remarks>
    /// Two ticks of tolerance rather than one so a single late tick does not read as a dead timer;
    /// the failure this distinguishes is a timer that stopped altogether, which loses every tick.
    /// </remarks>
    public static TimeSpan FreshTtlFloor => SessionTtl - Tick * 2;

    /// <summary>
    /// A TTL at or below this, read after <see cref="TwoTicks"/> of quiet, proves nothing renewed it.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="FreshTtlFloor"/>, and <see cref="Slack"/> below it so that a
    /// reading can never satisfy both.
    /// </remarks>
    public static TimeSpan DrainedTtlCeiling => SessionTtl - TwoTicks;

    /// <summary>
    /// How long to watch when the assertion is that something does <em>not</em> happen.
    /// </summary>
    /// <remarks>
    /// Two ticks, because everything periodic in presence is paced by the tick and a window shorter
    /// than two of them proves only that the first had not come round yet. Floored at three seconds
    /// so the compressed timings still leave room for an unwanted event to actually arrive and be
    /// seen — a negative assertion is only worth what the window gives the failure a chance to
    /// appear in.
    /// </remarks>
    public static TimeSpan NegativeWindow
        => Max(Tick * 2, TimeSpan.FromSeconds(3));

    /// <summary>
    /// The budget for a state change that should already be on its way: an RPC, a fold, a stream hop.
    /// </summary>
    /// <remarks>
    /// Deliberately a constant and deliberately generous. It is spent only when something is wrong —
    /// a poll that succeeds in fifty milliseconds costs fifty milliseconds — so there is nothing to
    /// gain by tightening it and a flaky suite to lose.
    /// </remarks>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>The same budget for a whole scripted sequence rather than one step.</summary>
    public static readonly TimeSpan LongSettle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The budget for a fold or a broadcast to land, where <see cref="Settle"/> would be spent too
    /// often to afford — inside a loop of thirty rounds, say.
    /// </summary>
    public static readonly TimeSpan Converge = TimeSpan.FromSeconds(3);

    /// <summary>
    /// "At once": the budget for something the product promises has no window at all, such as a
    /// deliberate sign-out taking effect.
    /// </summary>
    /// <remarks>
    /// Not derived, because it is not paced by anything — it is the assertion that the operation is
    /// synchronous with the call rather than waiting on a clock. Also used as a deliberate pause
    /// between two calls that should be read as one moment.
    /// </remarks>
    public static readonly TimeSpan Immediately = TimeSpan.FromSeconds(1);

    /// <summary>How often a poll re-reads while it waits.</summary>
    public static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(50);

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
        => left > right ? left : right;
}
