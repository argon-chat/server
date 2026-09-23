namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using ArgonContracts;

/// <summary>
/// What a catalogue row's stored document goes over Ion as.
/// </summary>
/// <remarks>
/// <para>Ion carries no JSON. A payload is stored as a document, checked against its kind's schema by
/// this build, and sent as the typed case of <see cref="ICosmeticPayload"/> — so what a client
/// receives is exactly what the schema allowed, with every optional number already the value the
/// client would have drawn it with.</para>
///
/// <para>A document that does not fit its kind is not sent at all: the row is left out of the
/// catalogue rather than sent half-understood.</para>
/// </remarks>
[TestFixture]
public class CosmeticPayloadWireTests
{
    private static readonly CosmeticKindRegistry Registry =
        CosmeticKindRegistry.FromAssembly(typeof(ICosmeticKind).Assembly);

    private static ICosmeticPayload? Wire(string kindKey, string json) => Registry.Find(kindKey)!.PayloadToWire(json);

    /// <summary>A kind with rows and no case would have every row of it quietly missing from the catalogue.</summary>
    [Test]
    public void Every_kind_with_rows_has_a_case_on_the_wire()
    {
        var samples = new Dictionary<string, string>
        {
            ["avatar.decoration"]  = """{"insetPct":10,"beneath":false}""",
            ["profile.frame"]      = """{"parts":[{"type":"ring","thickness":2,"colors":[-1]}]}""",
            ["option.font"]        = """{"cssFamily":"Inter"}""",
            ["option.text-effect"] = "{}"
        };

        var withRows = Registry.All.Where(kind => !kind.IsBare).Select(kind => kind.Key).ToList();

        Assert.That(samples.Keys, Is.EquivalentTo(withRows), "a kind with rows has no sample here — add one, and a case to the contract");

        foreach (var (kind, json) in samples)
        {
            Assert.That(Wire(kind, json), Is.Not.Null, kind);
        }
    }

    [Test]
    public void An_avatar_decoration_is_its_inset_and_its_side()
        => Assert.That(Wire("avatar.decoration", """{"insetPct":12,"beneath":true}"""),
            Is.EqualTo(new PayloadAvatarDecoration(12, true)));

    [Test]
    public void A_face_is_its_family()
        => Assert.That(Wire("option.font", """{"cssFamily":"Noto Sans JP"}"""), Is.EqualTo(new PayloadFont("Noto Sans JP")));

    [Test]
    public void A_treatment_carries_nothing()
        => Assert.That(Wire("option.text-effect", "{}"), Is.InstanceOf<PayloadTextEffect>());

    [Test]
    public void A_document_that_does_not_fit_its_kind_is_not_sent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Wire("avatar.decoration", """{"insetPct":90,"beneath":false}"""), Is.Null);
            Assert.That(Wire("option.font", """{"cssFamily":"Inter; } body {"}"""), Is.Null);
            Assert.That(Wire("profile.frame", """{"parts":[]}"""), Is.Null);
            Assert.That(Wire("profile.frame", "not a document"), Is.Null);
        });
    }

    [Test]
    public void A_frame_arrives_as_typed_parts_in_the_order_they_are_drawn()
    {
        var frame = Wire("profile.frame", """
            {"parts":[
              {"type":"surround","slot":"primary","slice":[8,8,8,8],"width":[4,0,4,0],"repeat":"round"},
              {"type":"prop","slot":"secondary","anchor":"topRight","w":40,"h":24,"dx":-6,
               "sprite":{"frames":8,"columns":4},"motion":{"kind":"sway","amount":2}},
              {"type":"ring","thickness":2,"colors":[-65536,-16776961],"glowPct":40,"opacityPct":80,"over":false}
            ]}
            """);

        Assert.That(frame, Is.InstanceOf<PayloadProfileFrame>());

        var parts = ((PayloadProfileFrame)frame!).parts;

        Assert.That(parts.Select(part => part.GetType()),
            Is.EqualTo(new[] { typeof(FrameSurround), typeof(FrameProp), typeof(FrameRing) }));

        var band = (FrameSurround)parts[0];
        var prop = (FrameProp)parts[1];
        var ring = (FrameRing)parts[2];

        Assert.Multiple(() =>
        {
            Assert.That(band.slot, Is.EqualTo(AssetSlot.Primary));
            Assert.That(band.width, Is.EqualTo(new FrameSides(4, 0, 4, 0)));
            Assert.That(band.repeat, Is.EqualTo(FrameRepeat.Round));
            Assert.That(band.outset, Is.Null);

            Assert.That(prop.slot, Is.EqualTo(AssetSlot.Secondary));
            Assert.That(prop.anchor, Is.EqualTo(FrameAnchor.TopRight));
            Assert.That(prop.dx, Is.EqualTo(-6));
            Assert.That(prop.sprite, Is.EqualTo(new FrameSprite(8, 4, 12, 0)));
            Assert.That(prop.motion, Is.EqualTo(new FrameMotion("sway", 2, 4000, 0)));

            Assert.That(ring.colors, Is.EqualTo(new[] { -65536, -16776961 }));
            Assert.That(ring.glowPct, Is.EqualTo((ushort)40));
            Assert.That(ring.opacityPct, Is.EqualTo((ushort)80));
            Assert.That(ring.over, Is.False);
        });
    }

    /// <summary>Nothing arrives as "absent" when the answer is known: the client's own defaults, filled in.</summary>
    [Test]
    public void What_a_part_leaves_out_arrives_as_what_the_client_would_draw()
    {
        var ring = (FrameRing)((PayloadProfileFrame)Wire("profile.frame",
            """{"parts":[{"type":"ring","thickness":3,"colors":[-1]}]}""")!).parts[0];

        Assert.Multiple(() =>
        {
            Assert.That(ring.over, Is.True);
            Assert.That(ring.opacityPct, Is.EqualTo((ushort)100));
            Assert.That(ring.angle, Is.EqualTo((ushort)135));
            Assert.That(ring.glowPct, Is.EqualTo((ushort)0));
            Assert.That(ring.inset, Is.Null);
            Assert.That(ring.motion, Is.Null);
        });
    }
}
