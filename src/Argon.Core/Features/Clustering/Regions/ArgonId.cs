namespace Argon.Features.Clustering.Regions;

/// <summary>
/// Identifiers that say which region they belong to.
/// </summary>
/// <remarks>
/// <para>Routing a call to another region needs to know which region owns the thing being addressed,
/// and the alternative to reading it off the key is a lookup: a cache on every entry node, an
/// invalidation path for it, and a cache miss on the hot path — all for a value that never changes
/// after the thing is created. So the region rides in the identifier.</para>
///
/// <para>These are UUIDv7 and stay UUIDv7. The version occupies four bits of byte 6 and the
/// timestamp occupies the six bytes before it; what this writes into is <c>rand_a</c>, the twelve
/// bits immediately after the version, which .NET fills with random data and nothing reads. The id
/// remains a valid v7, remains time-ordered, and keeps 62 of its 74 random bits.</para>
///
/// <para><b>The epoch is what makes existing data portable.</b> An identifier minted before this
/// scheme has a random <c>rand_a</c>, so it does not decode to "no region" — it decodes to an
/// arbitrary one, and no bit pattern distinguishes the two. What does distinguish them is the
/// timestamp the identifier already carries: everything older than the cutover was made when there
/// was one region, so it belongs to that one. Nothing has to be re-keyed, backfilled or migrated;
/// the identifiers already say when they were made.</para>
/// </remarks>
public static class ArgonId
{
    /// <summary>The largest region index that fits, since zero is spent on "not tagged".</summary>
    public const int MaxRegionIndex = 0xFFE;

    /// <summary>The region every identifier from before the cutover belongs to.</summary>
    /// <remarks>
    /// The first region is index zero and keeps it forever, which is what lets a second region be
    /// added beside it without touching a single existing row.
    /// </remarks>
    public const int OriginalRegionIndex = 0;

    private static int  regionIndex;

    // Unix milliseconds rather than a DateTimeOffset: Volatile only handles references and
    // primitives, and a torn read of the cutover would re-home whatever it was reading.
    private static long epochMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>The region this process stamps into the identifiers it mints.</summary>
    /// <remarks>
    /// Process-wide because it is a property of the process, the same way
    /// <see cref="ArgonDatacenter.Current"/> is, and reading it off an injected service would have
    /// meant threading a constructor parameter through thirty classes to deliver a number that is
    /// fixed before any of them are built.
    /// </remarks>
    public static int RegionIndex => Volatile.Read(ref regionIndex);

    /// <summary>
    /// Names the region this process mints for. Called once, during startup.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="OriginalRegionIndex"/> and it is the right answer for anything with
    /// one region, which includes every test and every deployment that has not split yet. What makes
    /// that safe rather than sloppy is that a deployment with peers is refused at startup unless this
    /// has been called with the region it configured — see <c>ArgonRegionRegistry</c>.
    /// </remarks>
    public static void UseRegion(int index, DateTimeOffset? cutover = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, MaxRegionIndex);

        Volatile.Write(ref regionIndex, index);
        Volatile.Write(ref epochMilliseconds, (cutover ?? DateTimeOffset.MaxValue).ToUnixTimeMilliseconds());
    }

    /// <summary>The cutover this process reads identifiers against.</summary>
    public static DateTimeOffset Epoch
        => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref epochMilliseconds));

    /// <summary>A new time-ordered identifier, tagged with this region.</summary>
    /// <remarks>
    /// The one way to mint an identifier for anything Argon stores or addresses. Raw
    /// <c>Guid.NewGuid()</c> remains correct for the things that are not identifiers — a nonce, a
    /// random suffix, a throwaway grain key — and wrong for everything else, because a v4 carries no
    /// timestamp and therefore no region.
    /// </remarks>
    public static Guid New() => Create(RegionIndex);

    /// <summary>
    /// A new identifier in the same region as an existing one.
    /// </summary>
    /// <remarks>
    /// <para>For the things that belong to something else rather than to whoever created them. A
    /// channel is the case this exists for: its messages live where its space lives, so the channel
    /// has to carry the space's region and not the region of the process that happened to serve the
    /// request. Space metadata is replicated everywhere, so that process can be anywhere.</para>
    ///
    /// <para>A sibling from before the cutover yields the original region, which is where it is.</para>
    /// </remarks>
    public static Guid NewIn(Guid sibling) => Create(RegionIndexOrOriginal(sibling, Epoch));

    /// <summary>A new time-ordered identifier tagged with <paramref name="regionIndex"/>.</summary>
    public static Guid Create(int regionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(regionIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(regionIndex, MaxRegionIndex);

        Span<byte> bytes = stackalloc byte[16];

        // Big-endian, which is the layout RFC 9562 describes and not the one Guid stores natively.
        // Getting this backwards would put the tag in the middle of the timestamp.
        if (!Guid.CreateVersion7().TryWriteBytes(bytes, bigEndian: true, out _))
            throw new InvalidOperationException("a Guid did not fit in sixteen bytes");

        // rand_a is the low nibble of byte 6 and all of byte 7. Byte 6's high nibble is the version
        // and is left exactly as it was.
        var tag = regionIndex + 1;

        bytes[6] = (byte)((bytes[6] & 0xF0) | ((tag >> 8) & 0x0F));
        bytes[7] = (byte)(tag & 0xFF);

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// The region an identifier was minted in.
    /// </summary>
    /// <param name="id">The identifier to read.</param>
    /// <param name="epoch">
    /// The instant tagging began. Anything older belongs to <see cref="OriginalRegionIndex"/>,
    /// whatever its bits happen to say. Pass <see cref="DateTimeOffset.MaxValue"/> to mean "tagging
    /// has not begun", which reads every identifier as original.
    /// </param>
    /// <remarks>
    /// Null only for an identifier that is not a UUIDv7 and therefore has no timestamp to judge by —
    /// a <c>Guid.NewGuid()</c> from before the sweep, or <see cref="Guid.Empty"/>. Those predate
    /// tagging by construction, so a caller treats null as original rather than as an error; it is
    /// returned separately because "I could not tell" and "region zero" are different things to log.
    /// </remarks>
    public static int? RegionIndexOf(Guid id, DateTimeOffset epoch)
    {
        Span<byte> bytes = stackalloc byte[16];

        if (!id.TryWriteBytes(bytes, bigEndian: true, out _))
            return null;

        // Version 7 in the high nibble of byte 6. Anything else carries no timestamp, so there is
        // nothing to compare against the epoch.
        if (bytes[6] >> 4 != 7)
            return null;

        if (TimestampOf(bytes) < epoch)
            return OriginalRegionIndex;

        var tag = ((bytes[6] & 0x0F) << 8) | bytes[7];

        // Post-epoch and untagged should not happen — everything minted after the cutover goes
        // through Create. Reading it as original is the safe answer rather than the clever one.
        return tag == 0 ? OriginalRegionIndex : tag - 1;
    }

    /// <summary>The region an identifier belongs to, with "could not tell" folded into original.</summary>
    public static int RegionIndexOrOriginal(Guid id, DateTimeOffset epoch)
        => RegionIndexOf(id, epoch) ?? OriginalRegionIndex;

    /// <summary>When a UUIDv7 was minted, from the 48 bits it carries for the purpose.</summary>
    public static DateTimeOffset? TimestampOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];

        if (!id.TryWriteBytes(bytes, bigEndian: true, out _) || bytes[6] >> 4 != 7)
            return null;

        return TimestampOf(bytes);
    }

    private static DateTimeOffset TimestampOf(ReadOnlySpan<byte> bigEndian)
    {
        var milliseconds = ((long)bigEndian[0] << 40)
                         | ((long)bigEndian[1] << 32)
                         | ((long)bigEndian[2] << 24)
                         | ((long)bigEndian[3] << 16)
                         | ((long)bigEndian[4] << 8)
                         | bigEndian[5];

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
