namespace Argon.Features.Clustering.Regions;

/// <summary>
/// The regions this deployment knows about, and how to reach each one.
/// </summary>
/// <remarks>
/// <para>A region is one Orleans cluster. Regions are grouped into zones, and a zone is the boundary
/// a request may be re-homed across — which is a data-residency question, not a latency one, so the
/// grouping is configuration rather than something derived from distance.</para>
///
/// <para>An absent section is a supported deployment and means exactly one region: this one. Nothing
/// here has a default that invents a peer, because a wrong peer address is worse than no peer at
/// all — it is a client that connects to something and calls grains on it.</para>
/// </remarks>
public sealed class ArgonRegionOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Argon:Regions";

    /// <summary>The region this process is in. Must name one of <see cref="Nodes"/>.</summary>
    /// <remarks>
    /// Defaults to <see cref="ArgonDatacenter.Current"/> so a single-region deployment configures
    /// nothing, and the environment variable that already names the datacenter keeps naming it.
    /// </remarks>
    public string Self { get; set; } = ArgonDatacenter.Current;

    /// <summary>Every region in the deployment, including this one, by name.</summary>
    public Dictionary<string, ArgonRegionNode> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a call into another region may take before it is given up on.
    /// </summary>
    /// <remarks>
    /// Deliberately much shorter than the Orleans default of thirty seconds, and deliberately its own
    /// setting rather than the local one. A remote region that is slow rather than down is the case
    /// that matters: with the default, requests pile up against it for half a minute each and the
    /// caller runs out of something long before the callee does.
    /// </remarks>
    public TimeSpan RemoteResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a region's gateway list is re-resolved.</summary>
    /// <remarks>
    /// The gateway address is a DNS name that moves as silos come and go, so this is how quickly a
    /// rolled deployment on the far side becomes visible. Short enough to follow a rollout, long
    /// enough not to be a DNS load test.
    /// </remarks>
    public TimeSpan GatewayRefreshPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest wait between attempts to reach a region that is not answering.</summary>
    public TimeSpan MaxReconnectBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The instant identifiers began carrying a region.
    /// </summary>
    /// <remarks>
    /// <para>This is what makes an existing deployment portable without touching a row. Identifiers
    /// minted before the scheme have a random <c>rand_a</c> and decode to an arbitrary region, so
    /// they cannot be read — but they carry the time they were made, and everything made before the
    /// cutover was made when there was one region. Below this instant the bits are ignored and the
    /// answer is the original region.</para>
    ///
    /// <para>Set it to any time after the tagging build is fully rolled out and before a second
    /// region exists. Both are easy: until there is a second region nothing reads a region at all,
    /// so the window is as wide as the gap between those two events. Once set it must never move —
    /// moving it re-homes everything on one side of it.</para>
    ///
    /// <para>Unset means tagging has not begun, and every identifier reads as the original region.
    /// That is the correct answer for a single-region deployment and is why this has no default.</para>
    /// </remarks>
    public DateTimeOffset? IdEpoch { get; set; }

    /// <summary>The epoch as the reader wants it: unset means nothing is tagged yet.</summary>
    public DateTimeOffset EffectiveIdEpoch => IdEpoch ?? DateTimeOffset.MaxValue;

    /// <summary>
    /// The index <see cref="Self"/> stamps into the identifiers it mints.
    /// </summary>
    /// <remarks>
    /// Zero when nothing is configured, which is what a single-region deployment gets and what makes
    /// adding a second region additive rather than a re-keying: the ids already minted keep meaning
    /// the region that has always been index zero.
    /// </remarks>
    public int SelfIndex
        => Nodes.TryGetValue(Self, out var node) ? node.Index : ArgonId.OriginalRegionIndex;

    /// <summary>The cutover, straight from configuration, for the same reason as the index.</summary>
    public static DateTimeOffset? EpochOf(IConfiguration configuration)
        => DateTimeOffset.TryParse(configuration[$"{SectionName}:{nameof(IdEpoch)}"], out var epoch)
            ? epoch
            : null;

    /// <summary>
    /// The same answer, straight from configuration.
    /// </summary>
    /// <remarks>
    /// Startup has to stamp the region before the container exists, because an identifier can be
    /// minted while the container is still being built.
    /// </remarks>
    public static int SelfIndexOf(IConfiguration configuration)
    {
        var self = configuration[$"{SectionName}:{nameof(Self)}"] ?? ArgonDatacenter.Current;
        var raw  = configuration[$"{SectionName}:{nameof(Nodes)}:{self}:{nameof(ArgonRegionNode.Index)}"];

        return int.TryParse(raw, out var index) ? index : ArgonId.OriginalRegionIndex;
    }

    /// <summary>The region a given index belongs to, or null if nothing claims it.</summary>
    public string? RegionOfIndex(int index)
    {
        foreach (var (name, node) in Nodes)
            if (node.Index == index)
                return name;

        // With no region list there is one region and it is index zero, whatever it is called.
        return Nodes.Count == 0 && index == 0 ? Self : null;
    }

    /// <summary>The regions other than <see cref="Self"/>.</summary>
    public IEnumerable<KeyValuePair<string, ArgonRegionNode>> Peers
        => Nodes.Where(n => !n.Key.Equals(Self, StringComparison.OrdinalIgnoreCase));

    public void Validate(IFeatureConfigurationReport report)
    {
        // No section at all is one region, and that is most deployments. Saying nothing here is the
        // difference between a warning every developer learns to ignore and one that means something.
        if (Nodes.Count == 0)
            return;

        report.Require(!string.IsNullOrWhiteSpace(Self), nameof(Self),
            "names no region; it must be the key of one of the configured nodes");

        report.Require(string.IsNullOrWhiteSpace(Self) || Nodes.ContainsKey(Self), nameof(Self),
            $"is '{Self}', which is not one of the configured regions ({string.Join(", ", Nodes.Keys)}). " +
            "A process that is not in the deployment it describes cannot decide what is local.");

        foreach (var (name, node) in Nodes)
        {
            report.Required(node.Zone, $"{nameof(Nodes)}:{name}:{nameof(ArgonRegionNode.Zone)}");
            report.Required(node.Gateway, $"{nameof(Nodes)}:{name}:{nameof(ArgonRegionNode.Gateway)}");

            if (!string.IsNullOrWhiteSpace(node.Gateway))
                report.Require(ArgonRegionNode.TryParseGateway(node.Gateway, out _, out _),
                    $"{nameof(Nodes)}:{name}:{nameof(ArgonRegionNode.Gateway)}",
                    $"is '{node.Gateway}'; it must be 'host:port', the address of that region's " +
                    "cluster gateway");

            report.Required(node.ClusterId, $"{nameof(Nodes)}:{name}:{nameof(ArgonRegionNode.ClusterId)}");

            report.RequireRange(node.Index, 0, ArgonId.MaxRegionIndex,
                $"{nameof(Nodes)}:{name}:{nameof(ArgonRegionNode.Index)}");
        }

        // Two regions on one index mint identifiers that claim each other's. Nothing detects that at
        // runtime — the id is well formed and names a region, just the wrong one — so it has to be
        // impossible to configure.
        foreach (var group in Nodes.GroupBy(n => n.Value.Index).Where(g => g.Count() > 1))
            report.Invalid($"regions {string.Join(", ", group.Select(g => g.Key))} share index " +
                           $"{group.Key}. The index is stamped into every identifier the region mints, " +
                           "so two regions cannot have the same one.");

        // The region this process claims to be in has to agree with the cluster this process is
        // actually running as. They come from different sections written by different hands, and a
        // disagreement is invisible at runtime: the local region would be dialled as if it were
        // remote and would never answer.
        if (Nodes.TryGetValue(Self, out var mine))
        {
            var identity = report.Read<LocalClusterIdentity>(ArgonClusterEndpoints.Section);
            var running  = identity.Id ?? ArgonClusterEndpoints.DefaultClusterId;

            report.Require(string.IsNullOrWhiteSpace(mine.ClusterId) || mine.ClusterId == running,
                $"{nameof(Nodes)}:{Self}:{nameof(ArgonRegionNode.ClusterId)}",
                $"is '{mine.ClusterId}', but this process runs as cluster '{running}' " +
                $"({ArgonClusterEndpoints.Section}:Id). One of the two is wrong.");

            var serviceId = identity.ServiceId ?? ArgonClusterEndpoints.DefaultServiceId;

            report.Require(mine.ResolvedServiceId() == serviceId,
                $"{nameof(Nodes)}:{Self}:{nameof(ArgonRegionNode.ServiceId)}",
                $"resolves to '{mine.ResolvedServiceId()}', but this process runs with service id " +
                $"'{serviceId}'. The service id names the service and has to be the same everywhere.");
        }

        // Two regions sharing a cluster id are one cluster as far as Orleans is concerned, and a
        // client aimed at either reaches whichever silo answered first. It is the kind of mistake
        // that looks like it works.
        var duplicated = Nodes
           .Where(n => !string.IsNullOrWhiteSpace(n.Value.ClusterId))
           .GroupBy(n => n.Value.ClusterId!, StringComparer.Ordinal)
           .Where(g => g.Count() > 1);

        foreach (var group in duplicated)
            report.Invalid($"regions {string.Join(", ", group.Select(g => g.Key))} share the cluster id " +
                           $"'{group.Key}'. Each region is its own cluster and needs its own id.");

        // With one region nothing reads a region out of an identifier, so the epoch is not needed and
        // asking for it would be noise. With two, an unset epoch means every identifier — including
        // the ones the second region is minting right now — reads as belonging to the first.
        report.Require(Nodes.Count <= 1 || IdEpoch is not null, nameof(IdEpoch),
            "is not set, and there is more than one region. Without it every identifier reads as " +
            "belonging to the original region, including the ones minted here. Set it to a time " +
            "after the tagging build was rolled out everywhere and before the second region existed.");

        report.RequireRange(RemoteResponseTimeout, TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(1),
            nameof(RemoteResponseTimeout));
        report.RequireRange(GatewayRefreshPeriod, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10),
            nameof(GatewayRefreshPeriod));
    }
}

/// <summary>Just enough of <c>Argon:Cluster</c> to check a process against its own region entry.</summary>
public sealed class LocalClusterIdentity
{
    public string? Id        { get; set; }
    public string? ServiceId { get; set; }
}

/// <summary>One region: which zone it belongs to, and how a client reaches its cluster.</summary>
public sealed class ArgonRegionNode
{
    /// <summary>
    /// The residency zone. Re-homing may move work between regions of one zone and never between
    /// zones.
    /// </summary>
    public string? Zone { get; set; }

    /// <summary>
    /// <c>host:port</c> of that region's cluster gateway.
    /// </summary>
    /// <remarks>
    /// A name rather than an address, and resolved on a timer rather than once, because the silos
    /// behind it are replaced by every deployment. In Kubernetes this is the headless service in
    /// front of the roles that expose a gateway; every address it resolves to is a gateway.
    /// </remarks>
    public string? Gateway { get; set; }

    /// <summary>
    /// The number this region stamps into the identifiers it mints.
    /// </summary>
    /// <remarks>
    /// <para>Written down rather than derived from the name, because every space, user and channel
    /// ever created in the region carries it. A hash of the name would be one line shorter and would
    /// silently re-home everything the day somebody renamed a region.</para>
    ///
    /// <para>Zero is a real index and the default, so the first region needs no configuration and the
    /// identifiers it has already minted stay correct when a second region is added beside it.</para>
    /// </remarks>
    public int Index { get; set; }

    /// <summary>
    /// The Orleans cluster id of that region. Required, with no default.
    /// </summary>
    /// <remarks>
    /// It has to match what that region's silos configure, and Orleans does not report a mismatch —
    /// a client with the wrong cluster id simply never finds a gateway it is allowed to talk to, and
    /// waits. Deriving one from the region name would look helpful and would be a guess about a
    /// value that lives in the other region's configuration, so there is no default. For the region
    /// this process is in, <see cref="ArgonRegionOptions.Validate"/> checks the two against each
    /// other; for the others, only the far side knows.
    /// </remarks>
    public string? ClusterId { get; set; }

    /// <summary>
    /// The Orleans service id, which must be the same in every region, and is by default.
    /// </summary>
    /// <remarks>
    /// Orleans keys grain storage and reminders on the service id, so it identifies the service
    /// rather than the deployment — the cluster id identifies the deployment. It is settable per node
    /// only so a migration can run with the two halves disagreeing for a while.
    /// </remarks>
    public string? ServiceId { get; set; }

    public string ResolvedServiceId()
        => string.IsNullOrWhiteSpace(ServiceId) ? ArgonClusterEndpoints.DefaultServiceId : ServiceId;

    /// <summary>Splits <c>host:port</c>, rejecting anything that is not exactly that.</summary>
    public static bool TryParseGateway(string? value, out string host, out int port)
    {
        host = "";
        port = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        if (!int.TryParse(value.AsSpan(separator + 1), out port) || port is <= 0 or > 65535)
            return false;

        host = value[..separator];
        return host.Length > 0;
    }
}
