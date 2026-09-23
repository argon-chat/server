namespace ArgonSharedLogicTest;

using System.Formats.Cbor;
using Argon.Features.Clustering;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

/// <summary>
/// Cosmetics crossing a grain call, through the serializer the cluster actually runs.
/// </summary>
/// <remarks>
/// <para>What a person wears and what a catalogue row looks like are unions, and they sit inside
/// other objects — a profile, a catalogue. Orleans names the type of a union handed to it directly;
/// one inside an object is Newtonsoft's, which cannot build an interface back. It failed every equip,
/// every prefetch of someone wearing anything and every catalogue read, and passed every test that
/// did not cross a grain.</para>
///
/// <para>A call inside one silo is deep-copied through the same serializer, so these are also what a
/// single-process test cluster does.</para>
/// </remarks>
[TestFixture]
public class CosmeticGrainSerializationTests
{
    private ServiceProvider services = null!;

    [OneTimeSetUp]
    public void Build() => services = new ServiceCollection().AddArgonSerializer().BuildServiceProvider();

    [OneTimeTearDown]
    public void Dispose() => services.Dispose();

    private T RoundTrip<T>(T value)
    {
        var serializer = services.GetRequiredService<Serializer>();
        return serializer.Deserialize<T>(serializer.SerializeToArray(value));
    }

    private T Copy<T>(T value) => services.GetRequiredService<DeepCopier>().Copy(value);

    private static readonly WornNickname Look =
        new(Guid.NewGuid(), null, new IonArray<int>([unchecked((int)0xFFFF0000), unchecked((int)0xFF0000FF)]),
            90, NicknameGradientShape.Conic, true, 700, -2);

    private static ArgonUserProfile Profile(IonArray<IWornCosmetic>? cosmetics)
        => new(Guid.NewGuid(), "away", null, null, null, "bio", IonArray<string>.Empty, IonArray<SpaceMemberArchetype>.Empty,
            null, null, null, null, null, null, DateTime.UtcNow, cosmetics);

    private static CosmeticCatalogue Catalogue()
        => new(new IonArray<string>(["profile.frame"]),
            new IonArray<CatalogueCosmetic>([
                new CatalogueCosmetic(Guid.NewGuid(), "profile.frame", "ember", "cosmetic.ember.name", null, "rare", 3,
                    new PayloadProfileFrame(new IonArray<IFramePart>([
                        new FrameSurround(true, 100, null, null, AssetSlot.Primary, new FrameSides(8, 8, 8, 8), new FrameSides(4, 0, 4, 0),
                            null, FrameRepeat.Round, false),
                        new FrameRing(false, 80, new FrameSides(2, 2, 2, 2), new FrameMotion("pulse", 3, 4000, 250), 2,
                            new IonArray<int>([-65536, -16776961]), 135, 40)
                    ])),
                    new IonArray<CosmeticAsset>([new CosmeticAsset(AssetSlot.Primary, "file-1")]),
                    null, false, true, IonArray<CosmeticText>.Empty)
            ]));

    [Test]
    public void A_profile_arrives_wearing_what_it_left_with()
    {
        var item    = new WornItem(Guid.NewGuid());
        var profile = Profile(new IonArray<IWornCosmetic>([item, Look]));

        foreach (var carried in new[] { RoundTrip(profile), Copy(profile) })
        {
            var worn = carried.cosmetics!.Value;

            Assert.That(worn.Select(x => x.GetType()), Is.EqualTo(new[] { typeof(WornItem), typeof(WornNickname) }));
            Assert.That(worn[0], Is.EqualTo(item));
            AssertSameLook((WornNickname)worn[1]);
        }
    }

    [Test]
    public void A_profile_wearing_nothing_and_one_from_before_cosmetics_both_arrive()
    {
        Assert.That(RoundTrip(Profile(IonArray<IWornCosmetic>.Empty)).cosmetics!.Value, Is.Empty);
        Assert.That(RoundTrip(Profile(null)).cosmetics, Is.Null);
    }

    /// <summary>The worn lists a space asks for, many people at once.</summary>
    [Test]
    public void A_batch_of_worn_lists_arrives_keyed_as_it_left()
    {
        var someone = Guid.NewGuid();
        var nobody  = Guid.NewGuid();

        var carried = RoundTrip(new Dictionary<Guid, IonArray<IWornCosmetic>>
        {
            [someone] = new([Look]),
            [nobody]  = IonArray<IWornCosmetic>.Empty
        });

        Assert.That(carried[nobody], Is.Empty);
        AssertSameLook((WornNickname)carried[someone].Single());
    }

    /// <summary>What an equip is called with: a union at the top of the call, not inside anything.</summary>
    [Test]
    public void An_equip_argument_arrives_as_its_case()
        => AssertSameLook((WornNickname)RoundTrip<IWornCosmetic>(Look));

    /// <summary>A frame is a union of parts inside a union of payloads inside a catalogue.</summary>
    [Test]
    public void A_catalogue_arrives_with_its_frames_typed()
    {
        var original = Catalogue();

        foreach (var carried in new[] { RoundTrip(original), Copy(original), ThroughCache(original) })
        {
            var row   = carried.items.Single();
            var parts = ((PayloadProfileFrame)row.payload).parts;

            Assert.Multiple(() =>
            {
                Assert.That(row.kindKey, Is.EqualTo("profile.frame"));
                Assert.That(row.ultima, Is.True);
                Assert.That(row.assets.Single(), Is.EqualTo(new CosmeticAsset(AssetSlot.Primary, "file-1")));
                Assert.That(parts.Select(x => x.GetType()), Is.EqualTo(new[] { typeof(FrameSurround), typeof(FrameRing) }));

                var ring = (FrameRing)parts[1];
                Assert.That(ring.over, Is.False);
                Assert.That(ring.motion, Is.EqualTo(new FrameMotion("pulse", 3, 4000, 250)));
                Assert.That(ring.colors, Is.EqualTo(new[] { -65536, -16776961 }));
            });
        }
    }

    /// <summary>How the catalogue is held in the cache: its own Ion encoding.</summary>
    private static CosmeticCatalogue ThroughCache(CosmeticCatalogue catalogue)
    {
        var cbor = new CborWriter();
        IonFormatterStorage<CosmeticCatalogue>.Write(cbor, catalogue);
        return IonFormatterStorage<CosmeticCatalogue>.Read(new CborReader(cbor.Encode()));
    }

    private static void AssertSameLook(WornNickname carried)
        => Assert.Multiple(() =>
        {
            Assert.That(carried.font, Is.EqualTo(Look.font));
            Assert.That(carried.effect, Is.Null);
            Assert.That(carried.colors!.Value, Is.EqualTo(new[] { unchecked((int)0xFFFF0000), unchecked((int)0xFF0000FF) }));
            Assert.That(carried.angle, Is.EqualTo(Look.angle));
            Assert.That(carried.shape, Is.EqualTo(NicknameGradientShape.Conic));
            Assert.That(carried.animate, Is.True);
            Assert.That(carried.weight, Is.EqualTo(Look.weight));
            Assert.That(carried.letterSpacing, Is.EqualTo(-2));
        });
}
