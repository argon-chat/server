namespace ArgonSharedLogicTest;

using Argon.Entities;
using ArgonContracts;
using ion.runtime;

/// <summary>
/// The merge behind <c>PatchBroadcastSettings</c>: a field the patch leaves out stays as stored, a
/// cleared one goes back to its default, the numbers are clamped, and the grain is told when the
/// result is what was stored already or when the channel targets itself.
/// </summary>
[TestFixture]
public class ChannelBroadcastPatchTests
{
    private static readonly Guid Self  = Guid.NewGuid();
    private static readonly Guid Alpha = Guid.NewGuid();
    private static readonly Guid Bravo = Guid.NewGuid();

    private static ChannelBroadcast Stored() => new()
    {
        Targets            = [Alpha],
        Overlap            = BroadcastOverlap.LOCK,
        DuckingDb          = -20,
        MaxTransmitSeconds = 60,
        Chirp              = true
    };

    private static IonPartial<BroadcastSettings> Patch() => new();

    private static IonArray<Guid> Ids(params Guid[] ids) => new(ids.ToList());

    [Test]
    public void Switching_the_mode_on_starts_from_the_defaults()
    {
        var defaults = ChannelBroadcast.Default();

        Assert.Multiple(() =>
        {
            Assert.That(defaults.Targets, Is.Empty);
            Assert.That(defaults.Overlap, Is.EqualTo(BroadcastOverlap.MIX));
            Assert.That(defaults.DuckingDb, Is.EqualTo(-8));
            Assert.That(defaults.MaxTransmitSeconds, Is.EqualTo(120));
            Assert.That(defaults.Chirp, Is.False);
        });
    }

    [Test]
    public void An_empty_patch_changes_nothing()
    {
        var (next, invalidTarget) = Stored().Apply(Patch(), Self);

        Assert.Multiple(() =>
        {
            Assert.That(next.SameAs(Stored()), Is.True);
            Assert.That(invalidTarget, Is.False);
        });
    }

    [Test]
    public void A_modified_field_replaces_only_itself()
    {
        var (next, _) = Stored().Apply(Patch().Modify(x => x.duckingDb, -12), Self);

        Assert.Multiple(() =>
        {
            Assert.That(next.DuckingDb, Is.EqualTo(-12));
            Assert.That(next.Targets, Is.EqualTo(new[] { Alpha }));
            Assert.That(next.Overlap, Is.EqualTo(BroadcastOverlap.LOCK));
            Assert.That(next.MaxTransmitSeconds, Is.EqualTo(60));
            Assert.That(next.Chirp, Is.True);
        });
    }

    [Test]
    public void Every_field_can_be_modified()
    {
        var patch = Patch()
           .Modify(x => x.targets, Ids(Bravo))
           .Modify(x => x.overlap, BroadcastOverlap.MIX)
           .Modify(x => x.duckingDb, -3)
           .Modify(x => x.maxTransmitSeconds, (int?)30)
           .Modify(x => x.chirp, false);

        var (next, _) = Stored().Apply(patch, Self);

        Assert.Multiple(() =>
        {
            Assert.That(next.Targets, Is.EqualTo(new[] { Bravo }));
            Assert.That(next.Overlap, Is.EqualTo(BroadcastOverlap.MIX));
            Assert.That(next.DuckingDb, Is.EqualTo(-3));
            Assert.That(next.MaxTransmitSeconds, Is.EqualTo(30));
            Assert.That(next.Chirp, Is.False);
        });
    }

    [Test]
    public void A_cleared_field_returns_to_its_default()
    {
        var patch = Patch()
           .Remove(x => x.targets)
           .Remove(x => x.overlap)
           .Remove(x => x.duckingDb)
           .Remove(x => x.maxTransmitSeconds)
           .Remove(x => x.chirp);

        var (next, invalidTarget) = Stored().Apply(patch, Self);

        Assert.Multiple(() =>
        {
            Assert.That(next.Targets, Is.Empty);
            Assert.That(next.Overlap, Is.EqualTo(BroadcastOverlap.MIX));
            Assert.That(next.DuckingDb, Is.EqualTo(-8));
            Assert.That(next.MaxTransmitSeconds, Is.Null, "cleared is no limit, not the 120 s the mode starts with");
            Assert.That(next.Chirp, Is.False);
            Assert.That(invalidTarget, Is.False);
        });
    }

    [Test]
    public void Targets_are_deduplicated_and_a_default_array_means_none()
    {
        var (deduplicated, _) = Stored().Apply(Patch().Modify(x => x.targets, Ids(Bravo, Alpha, Bravo)), Self);
        var (none, _)         = Stored().Apply(Patch().Modify(x => x.targets, default), Self);

        Assert.Multiple(() =>
        {
            Assert.That(deduplicated.Targets, Is.EqualTo(new[] { Bravo, Alpha }));
            Assert.That(none.Targets, Is.Empty);
        });
    }

    [TestCase(-50, -40)]
    [TestCase(5, 0)]
    [TestCase(-8, -8)]
    public void Ducking_is_clamped_to_minus_40_to_0(int given, int stored)
    {
        var (next, _) = Stored().Apply(Patch().Modify(x => x.duckingDb, given), Self);

        Assert.That(next.DuckingDb, Is.EqualTo(stored));
    }

    [TestCase(1, 10)]
    [TestCase(5000, 3600)]
    [TestCase(120, 120)]
    public void The_transmit_limit_is_clamped_to_10_to_3600(int given, int stored)
    {
        var (next, _) = Stored().Apply(Patch().Modify(x => x.maxTransmitSeconds, (int?)given), Self);

        Assert.That(next.MaxTransmitSeconds, Is.EqualTo(stored));
    }

    [Test]
    public void The_transmit_limit_modified_to_null_is_no_limit()
    {
        var (next, _) = Stored().Apply(Patch().Modify(x => x.maxTransmitSeconds, (int?)null), Self);

        Assert.That(next.MaxTransmitSeconds, Is.Null);
    }

    [Test]
    public void Restating_the_stored_values_is_no_change()
    {
        var patch = Patch()
           .Modify(x => x.targets, Ids(Alpha))
           .Modify(x => x.duckingDb, -20)
           .Modify(x => x.maxTransmitSeconds, (int?)60);

        var (next, _) = Stored().Apply(patch, Self);

        Assert.That(next.SameAs(Stored()), Is.True);
    }

    [Test]
    public void SameAs_compares_the_targets_by_content()
    {
        var stored = Stored();

        Assert.Multiple(() =>
        {
            Assert.That(stored.SameAs(stored with { Targets = [Alpha] }), Is.True);
            Assert.That(stored.SameAs(stored with { Targets = [Alpha, Bravo] }), Is.False);
            Assert.That(stored.SameAs(stored with { Chirp = false }), Is.False);
            Assert.That(stored.SameAs(stored with { MaxTransmitSeconds = null }), Is.False);
        });
    }

    [Test]
    public void Targeting_the_channel_itself_is_flagged()
    {
        var (_, self)  = Stored().Apply(Patch().Modify(x => x.targets, Ids(Bravo, Self)), Self);
        var (_, other) = Stored().Apply(Patch().Modify(x => x.targets, Ids(Bravo)), Self);

        Assert.Multiple(() =>
        {
            Assert.That(self, Is.True);
            Assert.That(other, Is.False);
        });
    }
}
