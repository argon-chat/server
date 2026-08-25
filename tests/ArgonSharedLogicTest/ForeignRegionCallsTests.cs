namespace ArgonSharedLogicTest;

using Argon.Features.Clustering.Regions;

/// <summary>
/// The one question the routing seam asks: does this id belong here.
/// </summary>
/// <remarks>
/// <para>Nothing routes across regions yet, so the seam only counts and complains. What these pin is
/// that it complains about the right things — because the failure it exists to catch is a deployment
/// that looks multi-region and serves everything locally, and a check that never fires is
/// indistinguishable from that.</para>
///
/// <para>The process statics are stamped and put back in a <c>finally</c>, the way
/// <see cref="ArgonIdTests"/> does: <c>ArgonId</c> carries the region and the epoch per process, and a
/// test that leaves either changed makes every later assertion in the run answer a question nobody
/// asked.</para>
///
/// <para><c>NonParallelizable</c> for the same reason and it is required, not decorative: the assembly
/// is <c>Parallelizable(ParallelScope.All)</c>, so without it these run beside <see cref="ArgonIdTests"/>
/// and beside each other while all of them are writing the same two statics. It was written without the
/// marker first, and the run that caught it failed on the one assertion whose answer depends on the
/// epoch nobody else was supposed to be touching.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class ForeignRegionCallsTests
{
    private static readonly DateTimeOffset Tagged = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An id minted by this region is not foreign, which is the answer on the hot path.
    /// </summary>
    [Test]
    public void An_identifier_of_this_region_is_not_foreign()
    {
        try
        {
            ArgonId.UseRegion(5, Tagged);

            Assert.That(ForeignRegionCalls.IsForeign(ArgonId.New()), Is.False);
        }
        finally
        {
            ArgonId.UseRegion(ArgonId.OriginalRegionIndex);
        }
    }

    /// <summary>
    /// An id minted by another region is, and this is the whole point of the seam.
    /// </summary>
    [Test]
    public void An_identifier_of_another_region_is_foreign()
    {
        try
        {
            ArgonId.UseRegion(5, Tagged);

            Assert.That(ForeignRegionCalls.IsForeign(ArgonId.Create(2)), Is.True,
                "a call for region 2 arriving at region 5 is the case that goes unnoticed today");
        }
        finally
        {
            ArgonId.UseRegion(ArgonId.OriginalRegionIndex);
        }
    }

    /// <summary>
    /// Everything minted before the cutover belongs to the original region, and is foreign only to a
    /// process that is not it.
    /// </summary>
    /// <remarks>
    /// This is the production database as it stands: every id in it predates the epoch, so the whole of
    /// it belongs to region zero. A region-five process calling into that data is exactly what the seam
    /// must notice, and a region-zero process doing it must stay silent — otherwise the first region
    /// added would drown the original one in warnings about its own rows.
    /// </remarks>
    [Test]
    public void A_legacy_identifier_belongs_to_the_original_region()
    {
        var legacy = Guid.NewGuid();

        try
        {
            ArgonId.UseRegion(ArgonId.OriginalRegionIndex, Tagged);
            Assert.That(ForeignRegionCalls.IsForeign(legacy), Is.False, "the original region owns it");

            ArgonId.UseRegion(5, Tagged);
            Assert.That(ForeignRegionCalls.IsForeign(legacy), Is.True, "and no other region does");
        }
        finally
        {
            ArgonId.UseRegion(ArgonId.OriginalRegionIndex);
        }
    }

    /// <summary>
    /// With no epoch declared nothing is foreign, however the id looks.
    /// </summary>
    /// <remarks>
    /// A single-region deployment never sets one, and that is the configuration every deployment is in
    /// today. If this were to answer otherwise, the seam would start reporting on a product that has
    /// exactly one region for it to be in.
    /// </remarks>
    [Test]
    public void Without_an_epoch_nothing_is_foreign()
        => Assert.Multiple(() =>
        {
            Assert.That(ForeignRegionCalls.IsForeign(ArgonId.Create(7)), Is.False);
            Assert.That(ForeignRegionCalls.IsForeign(ArgonId.New()), Is.False);
            Assert.That(ForeignRegionCalls.IsForeign(Guid.NewGuid()), Is.False);
        });
}
