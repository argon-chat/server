namespace ArgonComplexTest;

using Argon.Features.Clustering;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

/// <summary>
/// Registering a reminder in a cluster whose silos do not all host an <c>IRemindable</c> grain.
/// </summary>
/// <remarks>
/// <para>Reminder operations are not served by the silo that issues them. They are addressed to a
/// system target chosen by hashing across the whole cluster, so a silo that never called
/// <c>AddReminders</c> receives calls it can only reject —
/// <c>SystemTarget sys.svc.user.&lt;hash&gt;/&lt;address&gt; not active on this silo</c>.</para>
///
/// <para><b>Why this fixture pairs core with media.</b> Reminders used to be wired per role, on the
/// reasoning that a silo hosting no <c>IRemindable</c> grain has no reason to poll the reminder
/// table. <c>core</c> and <c>jobs</c> host them and had the service; <c>media</c>, <c>voice</c>,
/// <c>commerce</c> and <c>moderation</c> did not. Every silo still received a share of the calls.
/// A cluster of two <c>core</c> silos — which is what the migration fixture runs — cannot show
/// this, because both ends can answer. The pairing is the test.</para>
///
/// <para><b>What it cost in production.</b> Two failures with nothing in common on the surface.
/// <c>jobs</c> registered a reminder from a startup task, the call landed on <c>moderation</c>, and
/// the silo crash-looped. A user session on <c>core</c> asked for one, the call landed on
/// <c>commerce</c>, the rejection closed the SignalR connection, the client reconnected, activated
/// the session again and failed again — which reads on the client as the server refusing it, with
/// the word "reminder" appearing nowhere near the reconnect loop.</para>
///
/// <para><b>On the number of attempts.</b> One would be a coin toss: with two silos, a single grain
/// id has about an even chance of hashing to the one that can answer, so a single-shot test would
/// have passed half the time against the broken build and proved nothing on the other half. Sixteen
/// leaves that below one run in sixty thousand.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class ReminderRoutingTests : TestBase
{
    // Clear of RoleStartupTests, which walks upward from 21111 and again from 21511, and of
    // GrainMigrationTests at 22111 and 22131.
    private const int FirstSiloPort  = 22311;
    private const int SecondSiloPort = 22331;

    private const string ReminderCluster = "argon-test-reminders";

    /// <summary>Enough grain ids that every one of them hashing to `core` is not a plausible pass.</summary>
    private const int Attempts = 16;

    private RoleHost core  = null!;
    private RoleHost media = null!;

    private IGrainFactory Grains => core.Services.GetRequiredService<IGrainFactory>();

    /// <summary>
    /// One silo that hosts remindable grains and one that hosts none, in a cluster of their own.
    /// </summary>
    /// <remarks>
    /// Started one after the other, for the same reason the migration fixture does it: both run the
    /// database warm-up on the way up, and two of those racing on one schema is a flake with nothing
    /// to do with reminders.
    /// </remarks>
    [OneTimeSetUp]
    public async Task StartMixedCluster()
    {
        var settings = ArgonTestEnvironment.Instance.Host.Settings;

        core = new RoleHost(settings, ArgonRoleId.Core, FirstSiloPort, ReminderCluster);
        _ = core.Services.GetRequiredService<IGrainFactory>();

        media = new RoleHost(settings, ArgonRoleId.Media, SecondSiloPort, ReminderCluster);
        _ = media.Services.GetRequiredService<IGrainFactory>();

        await WaitForClusterAsync(2, TimeSpan.FromMinutes(2));
    }

    [OneTimeTearDown]
    public async Task StopCluster()
    {
        await media.DisposeAsync();
        await core.DisposeAsync();
    }

    [Test, CancelAfter(600_000)]
    public async Task A_reminder_registers_however_the_cluster_routes_it()
    {
        // The premise, asserted rather than assumed: with one silo this proves nothing at all,
        // because there is nowhere else for a call to be routed to.
        var silos = core.Services.GetRequiredService<ISiloStatusOracle>()
           .GetApproximateSiloStatuses(onlyActive: true);

        Assert.That(silos, Has.Count.EqualTo(2),
            "premise: two silos, one of which hosts no IRemindable grain");

        var failures = new List<string>();

        for (var i = 0; i < Attempts; i++)
        {
            // A session with no connections registers the grace reminder on detach — the shortest
            // path from a test to a real RegisterOrUpdateReminder. The key is well-formed
            // ("{userId}:{sid}") so activation parses it, and no user has to exist: a detach of a
            // connection that was never attached leaves the set empty, which is the branch that
            // arms the reminder.
            var session = Grains.GetGrain<IUserSessionGrain>($"{Guid.NewGuid()}:reminder-routing-{i}");

            try
            {
                await session.DetachConnectionAsync("never-attached");
            }
            catch (Exception e)
            {
                failures.Add($"attempt {i}: {e.GetType().Name}: {e.Message.Split(" Msg=")[0]}");
            }
        }

        Assert.That(failures, Is.Empty,
            "a reminder call was rejected by the silo it was routed to. The service has to be on "
          + "every silo or on none — hosting an IRemindable grain is not what decides it."
          + Environment.NewLine + string.Join(Environment.NewLine, failures.Distinct()));
    }

    private async Task WaitForClusterAsync(int expected, TimeSpan within)
    {
        var oracle   = core.Services.GetRequiredService<ISiloStatusOracle>();
        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline)
        {
            if (oracle.GetApproximateSiloStatuses(onlyActive: true).Count >= expected)
                return;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.Fail($"the reminder cluster did not reach {expected} active silos");
    }
}
