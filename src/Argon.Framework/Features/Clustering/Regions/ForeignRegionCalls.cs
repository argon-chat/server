namespace Argon.Features.Clustering.Regions;

using System.Diagnostics.Metrics;
using Argon.Features.Otel;

/// <summary>
/// Notices when a call addresses something that belongs to another region, and says so.
/// </summary>
/// <remarks>
/// <para>This does not route anything. It is the assertion that goes in first, while it cannot fire,
/// so that the day it can there is a signal instead of a silence.</para>
///
/// <para>The failure it stands against is the quiet one. With a second region configured, the region
/// registry connects, reports the peer healthy, and every request is still served wherever it landed —
/// a deployment that looks multi-region and behaves single-region, with no error path anywhere. What
/// that produces is two activations of the same grain in two clusters and rows written from both,
/// which is discovered later, in the database, by hand.</para>
///
/// <para>Costs nothing on the path it lives on. Both operands are process statics and the decode is a
/// stack write and two byte reads, so the ordinary answer — this is ours — is reached without a
/// dictionary, an allocation or a container lookup. Only the abnormal answer pays for anything, and
/// today the abnormal answer never happens: with one region every id decodes to index zero.</para>
///
/// <para>A routed call is ordinary and is counted, not logged: once there are two regions it happens
/// constantly, and a log line per cross-region call would bury the one thing worth reading. An id
/// nobody can serve is the opposite — rare, always a fault, and logged before the exception so the
/// grain and the region are in the log even if the throw is swallowed upstream.</para>
/// </remarks>
public static class ForeignRegionCalls
{
    private static readonly Counter<long> Observed = Instruments.Meter.CreateCounter<long>(
        InstrumentNames.ForeignRegionCalls,
        description: "Calls addressing an id owned by another region, by what happened to them");

    /// <summary>
    /// Whether <paramref name="grainKey"/> belongs to somewhere else.
    /// </summary>
    /// <remarks>
    /// Reads the region out of the id rather than asking the registry, because the id is the only
    /// thing the caller has: <c>ArgonId</c> writes the region into the UUIDv7 at mint time, and an id
    /// older than the epoch answers with the original region, which is correct — it was made when
    /// there was only one.
    /// </remarks>
    public static bool IsForeign(Guid grainKey)
        => ArgonId.RegionIndexOrOriginal(grainKey, ArgonId.Epoch) != ArgonId.RegionIndex;

    /// <summary>One call sent to the region that owns it.</summary>
    public static void Routed(Guid grainKey, string grainType)
        => Observed.Add(1,
            new KeyValuePair<string, object?>("grain", grainType),
            new KeyValuePair<string, object?>("region", ArgonId.RegionIndexOrOriginal(grainKey, ArgonId.Epoch)),
            new KeyValuePair<string, object?>("outcome", "routed"));

    /// <summary>
    /// One call whose owner cannot take it, logged before it is refused.
    /// </summary>
    /// <remarks>
    /// The logger arrives from the caller rather than from a static, and the call site resolves it only
    /// on this path — so the container lookup sits where it is rare and not on the one every request
    /// takes. Logged as well as thrown because the exception is the caller's to handle and may be
    /// turned into something blander before anyone sees it, while this line keeps the grain, the id and
    /// the region together.
    /// </remarks>
    public static void Unroutable(Guid grainKey, string grainType, ILogger logger)
    {
        var region = ArgonId.RegionIndexOrOriginal(grainKey, ArgonId.Epoch);

        Observed.Add(1,
            new KeyValuePair<string, object?>("grain", grainType),
            new KeyValuePair<string, object?>("region", region),
            new KeyValuePair<string, object?>("outcome", "unroutable"));

        logger.LogError(
            "{Grain} {Key} is owned by region index {Region} and this process, region {Self}, cannot "
          + "reach it. The call is refused rather than served here, because serving it would write to "
          + "the wrong region's database and look successful",
            grainType, grainKey, region, ArgonId.RegionIndex);
    }
}
