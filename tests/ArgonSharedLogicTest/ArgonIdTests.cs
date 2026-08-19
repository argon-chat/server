namespace ArgonSharedLogicTest;

using Argon.Features.Clustering.Regions;

/// <summary>
/// The identifier format, which is the one decision in the region work that cannot be taken back.
/// </summary>
/// <remarks>
/// Every space, user and channel ever created carries it. A mistake in the bit layout is not a bug
/// that gets fixed, it is a column that gets rewritten, so the layout is asserted rather than
/// trusted: that the version survives, that the timestamp survives, that the tag reads back, and —
/// the one that makes an existing deployment portable — that an identifier from before the scheme
/// reads as the original region instead of as an arbitrary one.
/// </remarks>
/// <remarks>
/// Not parallelizable, and that is a property of what is being tested rather than a convenience: the
/// region stamp is process-global — deliberately, because it is a property of the process — so two of
/// these running at once would each read the other's. The assembly is
/// <c>Parallelizable(ParallelScope.All)</c>, so saying so is required rather than decorative.
/// </remarks>
[TestFixture, NonParallelizable]
public class ArgonIdTests
{
    /// <summary>An epoch far in the past: everything is tagged.</summary>
    private static readonly DateTimeOffset Tagged = DateTimeOffset.UnixEpoch;

    /// <summary>An epoch far in the future: nothing is tagged, which is what legacy data looks like.</summary>
    private static readonly DateTimeOffset NotYet = DateTimeOffset.MaxValue;

    private static byte[] Bytes(Guid id)
    {
        var bytes = new byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    [Test]
    public void It_is_still_a_uuid_v7()
    {
        var bytes = Bytes(ArgonId.Create(1234));

        Assert.Multiple(() =>
        {
            Assert.That(bytes[6] >> 4, Is.EqualTo(7), "the version nibble must survive the tag");
            Assert.That(bytes[8] >> 6, Is.EqualTo(0b10), "the variant must survive too");
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(ArgonId.MaxRegionIndex)]
    public void A_tag_reads_back(int index)
        => Assert.That(ArgonId.RegionIndexOf(ArgonId.Create(index), Tagged), Is.EqualTo(index));

    [Test]
    public void An_index_that_does_not_fit_is_refused()
        => Assert.Multiple(() =>
        {
            Assert.That(() => ArgonId.Create(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => ArgonId.Create(ArgonId.MaxRegionIndex + 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });

    /// <summary>
    /// The timestamp is still the timestamp, which is what the epoch rule rests on.
    /// </summary>
    /// <remarks>
    /// The tag goes into <c>rand_a</c>, the twelve bits after the version. One byte further left and
    /// it would land in the timestamp, every identifier would claim to be from some other century,
    /// and the epoch comparison would sort them arbitrarily.
    /// </remarks>
    [Test]
    public void The_timestamp_survives_the_tag()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var id     = ArgonId.Create(ArgonId.MaxRegionIndex);
        var after  = DateTimeOffset.UtcNow.AddSeconds(1);

        var stamped = ArgonId.TimestampOf(id);

        Assert.That(stamped, Is.Not.Null);
        Assert.That(stamped!.Value, Is.InRange(before, after));
    }

    /// <summary>
    /// The whole porting story, in one assertion.
    /// </summary>
    /// <remarks>
    /// <para>An identifier minted before this scheme has a random <c>rand_a</c>. There is no bit
    /// pattern that says "untagged" — read naively it names an arbitrary region, and 4095 times out
    /// of 4096 that region is not the one it belongs to. That is why the cutover is a timestamp and
    /// not a marker: everything older than the epoch was made when there was one region.</para>
    ///
    /// <para>Sampled rather than asserted once, because the failure this guards against is
    /// probabilistic: a marker-based scheme would pass a single-case test 4095 times out of 4096.</para>
    /// </remarks>
    [Test]
    public void An_identifier_from_before_the_cutover_belongs_to_the_original_region()
    {
        for (var i = 0; i < 500; i++)
        {
            var legacy = Guid.CreateVersion7();

            Assert.That(ArgonId.RegionIndexOf(legacy, NotYet), Is.EqualTo(ArgonId.OriginalRegionIndex),
                $"a v7 minted before the epoch must read as original, and did not on attempt {i}");
        }
    }

    /// <summary>
    /// Even one this scheme made, if the epoch says it predates the cutover.
    /// </summary>
    /// <remarks>
    /// The epoch is the authority, not the bits. That matters during the rollout: identifiers minted
    /// by the new build before the epoch is set are read as original, which is correct, because until
    /// a second region exists the original region is the only one there is.
    /// </remarks>
    [Test]
    public void The_epoch_outranks_the_tag()
    {
        var tagged = ArgonId.Create(7);

        Assert.Multiple(() =>
        {
            Assert.That(ArgonId.RegionIndexOf(tagged, NotYet), Is.EqualTo(ArgonId.OriginalRegionIndex));
            Assert.That(ArgonId.RegionIndexOf(tagged, Tagged), Is.EqualTo(7));
        });
    }

    [Test]
    public void A_v4_carries_no_timestamp_and_so_no_region()
        => Assert.Multiple(() =>
        {
            Assert.That(ArgonId.RegionIndexOf(Guid.NewGuid(), Tagged), Is.Null);
            Assert.That(ArgonId.RegionIndexOf(Guid.Empty, Tagged), Is.Null);

            // The caller folds that into the original region, because a v4 predates tagging.
            Assert.That(ArgonId.RegionIndexOrOriginal(Guid.NewGuid(), Tagged),
                Is.EqualTo(ArgonId.OriginalRegionIndex));
        });

    /// <summary>
    /// Twelve bits are spent on the tag; the remaining sixty-two still have to be enough.
    /// </summary>
    /// <remarks>
    /// Generated in one tight loop on purpose, so most of these share a millisecond and the timestamp
    /// contributes nothing to telling them apart. .NET fills the rest of a v7 with random data rather
    /// than a counter, so this is the case where collisions would show up if the tag had eaten
    /// something load-bearing.
    /// </remarks>
    [Test]
    public void Identifiers_are_unique_within_a_millisecond()
    {
        const int count = 50_000;

        var seen = new HashSet<Guid>(count);

        for (var i = 0; i < count; i++)
            Assert.That(seen.Add(ArgonId.Create(3)), Is.True, "a minted identifier repeated");
    }

    /// <summary>
    /// And still sort by time, which is why they are v7 rather than v4.
    /// </summary>
    /// <remarks>
    /// Compared as byte sequences in RFC order rather than with <c>Guid.CompareTo</c>, which orders
    /// by .NET's mixed-endian field layout and would say something else entirely.
    /// </remarks>
    [Test]
    public async Task Identifiers_stay_time_ordered()
    {
        var first = ArgonId.Create(9);
        await Task.Delay(5);
        var second = ArgonId.Create(9);

        Assert.That(Bytes(first).AsSpan().SequenceCompareTo(Bytes(second)), Is.LessThan(0));
    }

    /// <summary>
    /// The tag does not disturb ordering between two identifiers from the same millisecond.
    /// </summary>
    /// <remarks>
    /// It cannot be otherwise — <c>rand_a</c> sits after the timestamp — but a layout mistake that put
    /// the tag before it would make identifiers from a high-index region sort ahead of everything,
    /// and message and channel listings are ordered by id.
    /// </remarks>
    [Test]
    public void A_high_region_index_does_not_sort_ahead_of_a_low_one()
    {
        var epoch = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var low  = Bytes(ArgonId.Create(1));
        var high = Bytes(ArgonId.Create(ArgonId.MaxRegionIndex));

        // Same millisecond in almost every run; when it is not, the timestamps order them and the
        // assertion below is about those instead, which is equally correct.
        Assert.That(low.AsSpan(0, 6).SequenceCompareTo(high.AsSpan(0, 6)), Is.LessThanOrEqualTo(0));

        Assert.That(ArgonId.RegionIndexOf(new Guid(high, bigEndian: true), epoch),
            Is.EqualTo(ArgonId.OriginalRegionIndex),
            "and the epoch still outranks whatever the tag says");
    }

    /// <summary>
    /// A channel belongs to its space, not to whoever happened to create it.
    /// </summary>
    /// <remarks>
    /// <para>The decision this pins: space metadata is replicated everywhere, so the activation that
    /// creates a channel can be in any region — but the channel's messages live where the space
    /// lives. Stamping the creating process's region would put a channel's messages in a region the
    /// space is not in, and nothing downstream would notice, because the id is well formed and names
    /// a real region.</para>
    ///
    /// <para>The process here is deliberately stamped as a <em>different</em> region from the space,
    /// which is the only arrangement in which the two possible answers differ.</para>
    /// </remarks>
    [Test]
    public void An_identifier_minted_for_a_sibling_takes_the_siblings_region()
    {
        try
        {
            ArgonId.UseRegion(5, Tagged);

            var space   = ArgonId.Create(2);
            var channel = ArgonId.NewIn(space);

            Assert.Multiple(() =>
            {
                Assert.That(ArgonId.RegionIndexOf(channel, Tagged), Is.EqualTo(2),
                    "the channel must follow the space");
                Assert.That(ArgonId.RegionIndexOf(ArgonId.New(), Tagged), Is.EqualTo(5),
                    "while an identifier of this process's own still follows the process");
            });
        }
        finally
        {
            ArgonId.UseRegion(ArgonId.OriginalRegionIndex);
        }
    }

    /// <summary>
    /// And a channel created in a space that predates the cutover stays with the original region.
    /// </summary>
    /// <remarks>
    /// The case an existing deployment hits on its first day of being multi-region: every space it
    /// has is older than the cutover, so every channel added to one belongs where that space already
    /// is, not where the region that served the request happens to be.
    /// </remarks>
    [Test]
    public void A_channel_added_to_a_legacy_space_stays_with_the_original_region()
    {
        try
        {
            // A second region, reading against a cutover that is still in the future.
            ArgonId.UseRegion(3);

            var legacySpace = Guid.CreateVersion7();

            Assert.That(ArgonId.RegionIndexOf(ArgonId.NewIn(legacySpace), Tagged),
                Is.EqualTo(ArgonId.OriginalRegionIndex));
        }
        finally
        {
            ArgonId.UseRegion(ArgonId.OriginalRegionIndex);
        }
    }

    /// <summary>
    /// The process-wide stamp, which is what <c>ArgonId.New()</c> uses.
    /// </summary>
    /// <remarks>
    /// Restored afterwards: it is process state, and every other fixture in this assembly mints
    /// identifiers expecting the original region.
    /// </remarks>
    [Test]
    public void New_stamps_the_region_the_process_was_told_about()
    {
        Assert.That(ArgonId.RegionIndexOf(ArgonId.New(), Tagged), Is.EqualTo(ArgonId.OriginalRegionIndex),
            "untouched, a process mints for the original region");

        try
        {
            ArgonId.UseRegion(11);

            Assert.That(ArgonId.RegionIndex, Is.EqualTo(11));
            Assert.That(ArgonId.RegionIndexOf(ArgonId.New(), Tagged), Is.EqualTo(11));
        }
        finally
        {
            ArgonId.UseRegion(ArgonId.OriginalRegionIndex);
        }
    }
}
