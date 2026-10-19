namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using ArgonContracts;
using ion.runtime;

/// <summary>
/// How much a profile pays, on the wire, for what its owner is wearing.
/// </summary>
/// <remarks>
/// <para>A profile rides along with every member list and every profile broadcast, so it is read by
/// the hundred, and whatever it carries is multiplied by that. The first version carried each
/// cosmetic's payload, files and names — a frame alone was hundreds of bytes — and the answer was to
/// carry references instead: the content is the catalogue's, read once per session.</para>
///
/// <para>These numbers are the budget that decision bought, measured with the formatter the wire
/// uses. A field added to <see cref="WornCosmetic"/> that breaks them is a field that should have gone
/// into the catalogue.</para>
/// </remarks>
[TestFixture]
public class WornCosmeticWireSizeTests
{
    private static int Encoded(IonArray<IWornCosmetic> worn) => WornCosmeticsWire.Write(worn).Length;

    /// <summary>What the cache stores comes back as it went in, case fields and all.</summary>
    [Test]
    public void What_is_cached_reads_back_as_it_was_written()
    {
        var worn = new IonArray<IWornCosmetic>([
            new WornItem(Guid.NewGuid()),
            new WornNickname(Guid.NewGuid(), null, new IonArray<int>([-32944, -16711936]),
                90, NicknameGradientShape.Linear, true, 700, -4)
        ]);

        var back = WornCosmeticsWire.Read(WornCosmeticsWire.Write(worn));

        Assert.That(back[0], Is.EqualTo(worn[0]));

        var name = (WornNickname)back[1];
        var sent = (WornNickname)worn[1];

        Assert.Multiple(() =>
        {
            Assert.That(name.font, Is.EqualTo(sent.font));
            Assert.That(name.effect, Is.Null);
            Assert.That(name.colors, Is.EqualTo(new[] { -32944, -16711936 }));
            Assert.That(name.shape, Is.EqualTo(NicknameGradientShape.Linear));
            Assert.That(name.letterSpacing, Is.EqualTo(-4));
        });
    }

    [Test]
    public void Nothing_worn_is_still_an_answer()
        => Assert.That(WornCosmeticsWire.Read(WornCosmeticsWire.Write(IonArray<IWornCosmetic>.Empty)), Is.Empty);

    [Test]
    public void A_worn_item_is_its_id_and_a_few_bytes_of_framing()
    {
        var one = Encoded(new IonArray<IWornCosmetic>([new WornItem(Guid.NewGuid())]));

        TestContext.Out.WriteLine($"one worn item: {one} bytes");

        Assert.That(one, Is.LessThanOrEqualTo(24));
    }

    /// <summary>The heaviest look a name can have: both axes chosen, six colours, every number set.</summary>
    [Test]
    public void The_heaviest_name_still_fits_in_a_hundred_bytes()
    {
        var name = new WornNickname(
            Guid.NewGuid(), Guid.NewGuid(),
            new IonArray<int>([unchecked((int)0xFFFF0000), unchecked((int)0xFF00FF00), unchecked((int)0xFF0000FF),
                               unchecked((int)0xFFFFFF00), unchecked((int)0xFF00FFFF), unchecked((int)0xFFFF00FF)]),
            360, NicknameGradientShape.Conic, true, 900, -10);

        var size = Encoded(new IonArray<IWornCosmetic>([name]));

        TestContext.Out.WriteLine($"heaviest name: {size} bytes");

        Assert.That(size, Is.LessThanOrEqualTo(100));
    }

    [Test]
    public void A_scene_with_its_reach_is_its_id_and_one_more_number()
    {
        var size = Encoded(new IonArray<IWornCosmetic>([new WornScene(Guid.NewGuid(), SceneReach.Card)]));

        TestContext.Out.WriteLine($"one worn scene with a reach: {size} bytes");

        Assert.That(size, Is.LessThanOrEqualTo(28));
    }

    /// <summary>
    /// A background, eight badges, a frame, a scene with its reach, a decoration, an orbit and a styled
    /// name — what somebody who has bought everything wears.
    /// </summary>
    /// <remarks>
    /// Thirteen references where there were three, which is what the budget grew by: the badges alone
    /// are eight rows, and each is an id.
    /// </remarks>
    [Test]
    public void Everything_this_build_can_put_on_one_person_stays_under_four_hundred_bytes()
    {
        var worn = new List<IWornCosmetic> { new WornItem(Guid.NewGuid()) };

        for (var badge = 0; badge < 8; badge++)
        {
            worn.Add(new WornItem(Guid.NewGuid()));
        }

        worn.Add(new WornItem(Guid.NewGuid()));
        worn.Add(new WornScene(Guid.NewGuid(), SceneReach.Content));
        worn.Add(new WornItem(Guid.NewGuid()));
        worn.Add(new WornItem(Guid.NewGuid()));
        worn.Add(new WornNickname(Guid.NewGuid(), Guid.NewGuid(),
            new IonArray<int>([unchecked((int)0xFFFF0000), unchecked((int)0xFF0000FF)]), 90, null, null, 700, null));

        var size = Encoded(new IonArray<IWornCosmetic>(worn));

        TestContext.Out.WriteLine($"a fully dressed profile: {size} bytes");

        Assert.That(size, Is.LessThanOrEqualTo(400));
    }
}
