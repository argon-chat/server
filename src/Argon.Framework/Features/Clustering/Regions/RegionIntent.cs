namespace Argon.Features.Clustering.Regions;

/// <summary>
/// What a region says about itself, as opposed to what anyone else can observe about it.
/// </summary>
/// <remarks>
/// <para>Intent is a property of the region and it is meant to travel: the region declares it once
/// and everyone subscribed to the bus hears the same sentence — every process of that region today,
/// and the other regions too once their NATS servers are gatewayed together, which is a deployment
/// change and not a code one (see <see cref="NatsRegionIntentChannel"/>). Reachability is a property
/// of the <em>pair</em> — "can I
/// reach them from here" — and travels nowhere, because a region asserting that it is reachable
/// asserts nothing about the network between us. Keeping the two apart is the whole point of this
/// type; they meet in exactly one place, <see cref="RegionAvailability.Merge"/>, and on exactly one
/// condition.</para>
///
/// <para>Nothing persists it. It is the running deployment's answer, declared by whoever is doing
/// the maintenance, and a region that restarts comes back <see cref="Active"/> — so the runbook
/// re-declares after a rollout rather than assuming a drain outlived it.</para>
///
/// <para>Numbered explicitly because these values go on the wire between regions, which are not
/// upgraded at the same instant. Renumbering them would make one region's "keep serving, take
/// nothing new" read as another region's "take everything".</para>
/// </remarks>
public enum RegionIntent
{
    /// <summary>Take new work. The default, and what silence means.</summary>
    Active = 0,

    /// <summary>
    /// Serve what is already homed here, and choose somewhere else for anything new.
    /// </summary>
    /// <remarks>
    /// The state planned maintenance needs and the one the deployment had no way to express. With
    /// only reachability to go on, a region going into maintenance looks healthy right up to the
    /// moment its last gateway goes unready and from then on looks exactly like a crash — draining
    /// and dying are the same picture. This says which of the two it is, in advance, out loud.
    /// </remarks>
    Draining = 1
}

/// <summary>One region's declaration about itself, as it goes over the wire.</summary>
/// <remarks>
/// <para>Carries intent and nothing else. There is deliberately no field here for how healthy the
/// sender believes it is: a region's opinion of its own reachability is not usable by anyone,
/// because reachability is not a property the sender holds.</para>
///
/// <para><see cref="DeclaredAt"/> is the instant of the <em>declaration</em>, not of the message. A
/// repeat carries the original instant, which is what keeps last-writer-wins stable while every
/// process of a region repeats the same sentence on its own timer.</para>
/// </remarks>
public sealed record RegionIntentAnnouncement(string Region, RegionIntent Intent, DateTimeOffset DeclaredAt);

/// <summary>
/// The merge rule: reachability and intent are different signals, and combining them wrongly is
/// worse than not having intent at all.
/// </summary>
/// <remarks>
/// <para>Both signals answer "how much work may this region be given", so both can be placed on one
/// scale — and once they are, the only safe way to combine them is to take the lower of the two.
/// An advertisement may make a region <em>less</em> usable and never more.</para>
///
/// <para>That asymmetry is the whole safety argument. An announcement is a claim by a third party,
/// delivered over a bus, possibly minutes old, possibly from a process that has since died, and in
/// the worst case simply wrong. If a claim could raise a region's status, a stale or lying peer
/// would be able to make a region this process demonstrably cannot reach look routable, and calls
/// would be sent into a hole. Because it can only lower, the worst a bad announcement can do is
/// cost throughput.</para>
///
/// <para>Local reachability is irreplaceable for the same reason and is never advertised: no peer
/// can answer "can <em>this</em> process reach that region" on this process's behalf.</para>
/// </remarks>
public static class RegionAvailability
{
    /// <summary>
    /// How much work a region may be given, as a number, so that two signals can be compared.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off <see cref="RegionStatus"/>'s declaration order. The enum's
    /// numbers are a wire and log concern and have to stay put; this ordering is a routing concern
    /// and is the one thing <see cref="Merge"/> depends on, so it says what it means instead of
    /// depending on the order somebody happened to type the members in.
    /// </remarks>
    public static int Rank(this RegionStatus status) => status switch
    {
        RegionStatus.Offline    => 0,
        RegionStatus.Connecting => 1,
        RegionStatus.Draining   => 2,
        RegionStatus.Online     => 3,
        _                       => 0
    };

    /// <summary>The best a region can be said to be while it is declaring this intent.</summary>
    public static RegionStatus Ceiling(this RegionIntent intent) => intent switch
    {
        RegionIntent.Draining => RegionStatus.Draining,
        _                     => RegionStatus.Online
    };

    /// <summary>The less usable of two statuses.</summary>
    public static RegionStatus Least(RegionStatus a, RegionStatus b)
        => a.Rank() <= b.Rank() ? a : b;

    /// <summary>
    /// <c>min(local reachability, advertised intent)</c>, and nothing else.
    /// </summary>
    /// <param name="reachability">
    /// What this process observes through its own connections. Never comes off the bus.
    /// </param>
    /// <param name="intent">What the region last said about itself. Silence is <see cref="RegionIntent.Active"/>.</param>
    public static RegionStatus Merge(RegionStatus reachability, RegionIntent intent)
        => Least(reachability, intent.Ceiling());

    /// <summary>Whether a call may be made into the region at all.</summary>
    /// <remarks>
    /// A draining region is usable: it still holds the activations that were placed there and still
    /// answers for them. Refusing it would turn a planned drain into the outage the drain exists to
    /// avoid.
    /// </remarks>
    public static bool IsUsable(this RegionStatus status)
        => status is RegionStatus.Online or RegionStatus.Draining;

    /// <summary>Whether the region may be chosen for work that is not homed anywhere yet.</summary>
    public static bool AcceptsNewWork(this RegionStatus status)
        => status is RegionStatus.Online;
}

/// <summary>Every region's declared intent, as this process last heard it, and this region's own.</summary>
public interface IRegionIntents
{
    /// <summary>What this region is currently saying about itself.</summary>
    RegionIntent Local { get; }

    /// <summary>What a region last said about itself. Never heard from is <see cref="RegionIntent.Active"/>.</summary>
    RegionIntent IntentOf(string region);

    /// <summary>
    /// The local declaration, for repeating, or null if this process has never been told one.
    /// </summary>
    /// <remarks>
    /// Null rather than a synthesised <see cref="RegionIntent.Active"/>: a process that was never
    /// told anything must stay silent, because announcing a default would let a pod that restarted
    /// during maintenance overrule the drain the rest of its region is under.
    /// </remarks>
    RegionIntentAnnouncement? Declaration { get; }

    /// <summary>Declares the local region's intent and puts it on the announcement bus.</summary>
    ValueTask DeclareAsync(RegionIntent intent, CancellationToken ct = default);

    /// <summary>Records what somebody else announced. False if it was not newer than what is held.</summary>
    bool Record(RegionIntentAnnouncement announcement);
}

/// <summary>
/// The local replica: one entry per region, last declaration wins.
/// </summary>
/// <remarks>
/// <para>Holds no reachability and cannot be asked for any. Everything here arrived because a region
/// said it about itself.</para>
///
/// <para>Announcements about the <em>local</em> region are recorded like anyone else's, which is
/// what makes a drain a property of the region rather than of whichever pod the operator happened to
/// reach: one process declares, the announcement comes back through the bus, and the region's other
/// processes adopt it and start repeating it themselves. Without that, "is this region draining"
/// would have a different answer per pod.</para>
/// </remarks>
public sealed class RegionIntents(
    IOptions<ArgonRegionOptions> options,
    IRegionIntentChannel channel,
    ILogger<RegionIntents> logger) : IRegionIntents
{
    private readonly ConcurrentDictionary<string, RegionIntentAnnouncement> declarations
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly string self = options.Value.Self;

    public RegionIntent Local => IntentOf(self);

    public RegionIntent IntentOf(string region)
        => declarations.TryGetValue(region, out var declaration) ? declaration.Intent : RegionIntent.Active;

    public RegionIntentAnnouncement? Declaration
        => declarations.TryGetValue(self, out var declaration) ? declaration : null;

    public async ValueTask DeclareAsync(RegionIntent intent, CancellationToken ct = default)
    {
        var announcement = new RegionIntentAnnouncement(self, intent, DateTimeOffset.UtcNow);

        // Applied here before it is published, so that what this process answers does not depend on
        // a round trip through a bus that may be down. The declaration is a decision, not a request.
        Record(announcement);

        logger.LogInformation("Region '{Region}' declares {Intent}", self, intent);

        await channel.PublishAsync(announcement, ct);
    }

    public bool Record(RegionIntentAnnouncement announcement)
    {
        if (string.IsNullOrWhiteSpace(announcement.Region))
            return false;

        while (true)
        {
            if (!declarations.TryGetValue(announcement.Region, out var held))
            {
                if (declarations.TryAdd(announcement.Region, announcement))
                    return true;

                continue;
            }

            if (!Supersedes(announcement, held))
                return false;

            if (declarations.TryUpdate(announcement.Region, announcement, held))
                return true;
        }
    }

    /// <summary>Whether an announcement is newer than the one already held for that region.</summary>
    /// <remarks>
    /// Ordered by the instant of the declaration rather than of the message, because every process
    /// of a region repeats its region's declaration and a repeat must never beat a newer decision.
    /// A tie goes to the more restrictive of the two: two declarations stamped the same instant is a
    /// clock, not a decision, and the restrictive one is the one that cannot break anything.
    /// </remarks>
    private static bool Supersedes(RegionIntentAnnouncement candidate, RegionIntentAnnouncement held)
        => candidate.DeclaredAt > held.DeclaredAt
        || (candidate.DeclaredAt == held.DeclaredAt
         && candidate.Intent.Ceiling().Rank() < held.Intent.Ceiling().Rank());
}
