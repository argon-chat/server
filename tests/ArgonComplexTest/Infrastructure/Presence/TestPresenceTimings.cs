namespace ArgonComplexTest.Infrastructure.Presence;

using Argon.Features.Logic;

/// <summary>
/// The presence clocks the integration host runs on, and the configuration keys that set them.
/// </summary>
/// <remarks>
/// <para>Presence is almost entirely a story about time: a key that lapses, a tick that renews it, a
/// grace that outlasts a reconnect, a deadline that turns "statusless" into "Online". Every one of
/// those is worth a test and none of them can be observed faster than the clock it is paced by, so
/// at shipped values — a two-minute session TTL, a fifteen-second tick, a one-minute grace reminder,
/// a ten-minute activity — the eleven presence fixtures spent over twenty minutes of wall time doing
/// nothing but waiting, most of it inside a handful of tests.</para>
///
/// <para>The numbers below are the shipped ones an order of magnitude down. They are set through
/// configuration, so the code under test is the production code with production wiring; nothing is
/// stubbed and no branch is skipped. What keeps the assertions honest is that the fixtures never
/// mention a number: they ask <see cref="PresenceProbe.Timings"/> what the host is running on and
/// express every wait as a ratio of it — a tick, two ticks, a TTL plus a tick, a grace plus a
/// reminder floor. Those ratios are the same at both scales, which is exactly why the same test
/// passes for the same reason in one second that it used to pass for in thirty.</para>
///
/// <para>They are also constrained: <see cref="PresenceTimingOptions.Validate"/> runs against this
/// section at host start like any other, so a set of values that would break the subsystem's own
/// invariants (a tick that does not fit inside its TTL, a grace below the reminder floor) fails the
/// host rather than producing a suite that quietly tests something else.</para>
/// </remarks>
public static class TestPresenceTimings
{
    /// <summary>
    /// What <c>ReminderOptions.MinimumReminderPeriod</c> is lowered to for the test host.
    /// </summary>
    /// <remarks>
    /// Orleans' own floor is one minute and <c>RegisterOrUpdateReminder</c> throws below it, so this
    /// is the real lower bound on <see cref="PresenceTimingOptions.GracePeriod"/> — and the reason
    /// the options class mirrors it: the two have to be lowered together or the grace is either
    /// rejected by Orleans or never observed by the fixture. Kept here as one constant so the host's
    /// <c>Configure&lt;ReminderOptions&gt;</c> and the <c>Presence:ReminderFloor</c> setting below
    /// cannot drift apart.
    /// </remarks>
    public static readonly TimeSpan ReminderFloor = TimeSpan.FromSeconds(2);

    /// <summary>The fast presence section, as <c>UseSetting</c> pairs.</summary>
    /// <remarks>
    /// <c>UseSetting</c> rather than an in-memory configuration source, for the reason
    /// <see cref="RoleHost"/> spells out: features bind their options while the container is being
    /// built, which is before <c>WebApplicationFactory</c> applies its configuration callbacks.
    /// Durations are written in <c>hh:mm:ss</c> because the configuration binder reads a bare number
    /// as a count of days.
    /// </remarks>
    public static IEnumerable<(string Setting, string Value)> Settings
    {
        get
        {
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.SessionTtl)}", "00:00:12");
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.RefreshPeriod)}", "00:00:02");
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.HeartbeatDebounce)}", "00:00:02");
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.GracePeriod)}", "00:00:05");
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.ReminderFloor)}", "00:00:02");
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.DeactivationDelay)}", "00:00:20");
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.StatusDeadline)}", "00:00:02");
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.ActivityTtl)}", "00:00:20");

            // Above ActivityTtl, as Validate requires, and in the shipped ratio (15 min against 10)
            // rather than merely above it — the two numbers together are what a test of the lease
            // costs in wall clock, since the slowest thing the bound promises is "an activity nobody
            // re-announces is gone within the window plus a lifetime". Left at the shipped 15 min the
            // window could not close inside any fixture at all, so the renewal was unconditional for
            // the whole suite and the bound — the change it exists to guard — was asserted nowhere.
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.ActivityReassertWindow)}", "00:00:30");

            // Compressed like everything else here, so the fixture that waits the floor out costs
            // seconds rather than the shipped three minutes — but not compressed as far as the rest.
            // This is the one clock that is paced by the CLIENT rather than by a server interval, and
            // the suite's clients are the fixtures themselves: a test that holds a session open while
            // waiting for something else sends nothing in the meantime, exactly like a dead transport.
            // So the floor has to clear the ordinary waits a live session sits through —
            // PresenceWaits.Settle, GraceAndABit, OfflineDeadline — or the sweep starts deciding tests
            // that are about something else entirely. Twenty seconds clears all of those with room,
            // is ten refresh periods (the shipped floor is twelve of the client's heartbeats), and
            // still leaves the sweep observable inside one test's budget.
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.StaleConnectionAfter)}", "00:00:20");

            // The friends seed's own debounce. Compressed because two fixtures reconnect a client
            // within a second or two of its first connect and expect to be seeded again; at the
            // shipped thirty seconds the debounce would decide those tests rather than the behaviour
            // they are about.
            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.FriendPushDebounce)}", "00:00:02");

            yield return ($"{PresenceTimingOptions.SectionName}:{nameof(PresenceTimingOptions.LastBroadcastTtl)}", "00:05:00");
        }
    }
}
