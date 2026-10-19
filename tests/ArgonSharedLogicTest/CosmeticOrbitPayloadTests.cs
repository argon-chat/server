namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using ArgonContracts;

/// <summary>
/// What an orbit's payload refuses, in so many words, and what Ion carries of one it accepts.
/// </summary>
/// <remarks>
/// <para>An orbit is drawn wherever a face is, so its refusals are mostly about reach: a figure that
/// lands on the neighbouring message is a number somebody typed, and the arithmetic that catches it
/// is the same the client does to reserve room. Each refusal below starts from a satellite that
/// passes and changes one thing.</para>
///
/// <para>Numbers are printed with the invariant culture here so that a decimal point reads the same
/// on every machine.</para>
/// </remarks>
[TestFixture]
[SetCulture("")]
public class CosmeticOrbitPayloadTests
{
    private static readonly CosmeticKindDefinition OrbitKind =
        CosmeticKindRegistry.FromAssembly(typeof(ICosmeticKind).Assembly).Find("avatar.orbit")!;

    /// <summary>
    /// A square figure exactly at the reach ceiling when it lies square to the screen: 160 out, half
    /// of 80 across, less the 50 that is the face itself, is 150.
    /// </summary>
    private static CosmeticOrbitSatellite Satellite(Action<CosmeticOrbitSatellite>? change = null)
    {
        var satellite = new CosmeticOrbitSatellite
        {
            Type      = "satellite",
            Slot      = "Primary",
            RadiusPct = 160d,
            WidthPct  = 80d,
            HeightPct = 80d,
            PeriodMs  = 6000
        };

        change?.Invoke(satellite);

        return satellite;
    }

    private static AvatarOrbitPayload Orbit(params CosmeticOrbitSatellite[] satellites) => new() { Satellites = satellites };

    private static AvatarOrbitPayload Orbit(Action<CosmeticOrbitSatellite> change) => Orbit(Satellite(change));

    private static IReadOnlyList<string> ErrorsOf(AvatarOrbitPayload orbit)
    {
        var report = new CosmeticPayloadReport();

        orbit.Validate(report);

        return report.Errors;
    }

    private static TestCaseData Refused(string because, AvatarOrbitPayload orbit, params string[] refusals)
        => new TestCaseData(orbit, refusals).SetArgDisplayNames(because);

    private static TestCaseData Accepted(string because, AvatarOrbitPayload orbit)
        => new TestCaseData(orbit).SetArgDisplayNames(because);

    [TestCaseSource(nameof(Refusals))]
    public void A_value_outside_its_rule_is_refused_in_so_many_words(AvatarOrbitPayload orbit, string[] refusals)
        => Assert.That(ErrorsOf(orbit), Is.EqualTo(refusals));

    [TestCaseSource(nameof(Acceptances))]
    public void A_value_inside_its_rule_is_accepted(AvatarOrbitPayload orbit)
        => Assert.That(ErrorsOf(orbit), Is.Empty);

    private static IEnumerable<TestCaseData> Refusals()
    {
        yield return Refused("no satellites", new AvatarOrbitPayload(), "satellites needs at least one figure for there to be an orbit");

        yield return Refused("an empty list of satellites", Orbit(), "satellites needs at least one figure for there to be an orbit");

        yield return Refused("nine satellites", Orbit(Enumerable.Range(0, 9).Select(_ => Satellite()).ToArray()),
            "satellites is capped at 8, which is what the console's own list holds");

        yield return Refused("a negative still size", new AvatarOrbitPayload { Satellites = [Satellite()], MinSizePx = -1 },
            "minSizePx is -1, and the range is 0 to 512");

        yield return Refused("a still size past a drawing", new AvatarOrbitPayload { Satellites = [Satellite()], MinSizePx = 513 },
            "minSizePx is 513, and the range is 0 to 512");

        yield return Refused("a body that is not a satellite, which is checked no further", Orbit(satellite =>
            {
                satellite.Type      = "moon";
                satellite.RadiusPct = -1d;
            }),
            "satellites[0].type is 'satellite'");

        yield return Refused("a body of no type", Orbit(satellite => satellite.Type = null), "satellites[0].type is 'satellite'");

        yield return Refused("a satellite with no file", Orbit(satellite => satellite.Slot = null),
            "satellites[0].slot names no file, and a satellite is a picture");

        yield return Refused("a satellite with an empty file name", Orbit(satellite => satellite.Slot = string.Empty),
            "satellites[0].slot names no file, and a satellite is a picture");

        yield return Refused("a slot that does not exist", Orbit(satellite => satellite.Slot = "Nowhere"),
            "satellites[0].slot 'Nowhere' is not an asset slot");

        yield return Refused("a slot by a number no slot has", Orbit(satellite => satellite.Slot = "9"),
            "satellites[0].slot '9' is not an asset slot");

        // Enum.TryParse reads all of these as a member, and none of them is a name anybody wrote.
        foreach (var slot in new[] { "0", "4", "7", "Primary, Secondary", " Primary", "Primary ", "primary", "PRIMARY" })
        {
            yield return Refused($"a slot spelled '{slot}'", Orbit(satellite => satellite.Slot = slot),
                $"satellites[0].slot '{slot}' is not an asset slot");
        }

        yield return Refused("a slot the kind has no file for", Orbit(satellite => satellite.Slot = "Poster"),
            "satellites[0].slot 'Poster' is not a slot an orbit has a file in");

        yield return Refused("a figure that is not there", Orbit(Satellite(), null!, Satellite()),
            "satellites[1] is null");

        yield return Refused("only figures that are not there", Orbit(null!, null!),
            "satellites[0] is null",
            "satellites[1] is null");

        yield return Refused("no radius, so no reach to measure", Orbit(satellite => satellite.RadiusPct = null),
            "satellites[0].radiusPct is missing");

        yield return Refused("a negative radius", Orbit(satellite => satellite.RadiusPct = -1d),
            "satellites[0].radiusPct is -1, and the range is 0 to 200");

        yield return Refused("a radius past twice the face", Orbit(satellite => satellite.RadiusPct = 201d),
            "satellites[0].radiusPct is 201, and the range is 0 to 200");

        yield return Refused("no width, so no reach to measure", Orbit(satellite =>
            {
                satellite.WidthPct  = null;
                satellite.RadiusPct = 200d;
            }),
            "satellites[0].widthPct is missing");

        yield return Refused("a figure of no width", Orbit(satellite => satellite.WidthPct = 0d),
            "satellites[0].widthPct is 0, and the range is 1 to 200");

        yield return Refused("no height, so no reach to measure", Orbit(satellite =>
            {
                satellite.HeightPct = null;
                satellite.RadiusPct = 200d;
            }),
            "satellites[0].heightPct is missing");

        yield return Refused("a figure twice the face and more", Orbit(satellite => satellite.HeightPct = 201d),
            "satellites[0].heightPct is 201, and the range is 1 to 200");

        yield return Refused("no period", Orbit(satellite => satellite.PeriodMs = null), "satellites[0].periodMs is missing");

        yield return Refused("a strobe", Orbit(satellite => satellite.PeriodMs = 239), "satellites[0].periodMs is 239, and the range is 240 to 120000");

        yield return Refused("a period past two minutes", Orbit(satellite => satellite.PeriodMs = 120_001),
            "satellites[0].periodMs is 120001, and the range is 240 to 120000");

        yield return Refused("a ring tipped past edge-on", Orbit(satellite => satellite.TiltDeg = 91d),
            "satellites[0].tiltDeg is 91, and the range is 0 to 90");

        yield return Refused("a ring tipped backwards", Orbit(satellite => satellite.TiltDeg = -1d),
            "satellites[0].tiltDeg is -1, and the range is 0 to 90");

        yield return Refused("a lean past half a turn", Orbit(satellite => satellite.YawDeg = 181d),
            "satellites[0].yawDeg is 181, and the range is -180 to 180");

        yield return Refused("a rock past half a turn", Orbit(satellite => satellite.YawSwingDeg = 181d),
            "satellites[0].yawSwingDeg is 181, and the range is 0 to 180");

        yield return Refused("a rock too quick", Orbit(satellite => satellite.YawSwingMs = 239),
            "satellites[0].yawSwingMs is 239, and the range is 240 to 120000");

        yield return Refused("a rock past two minutes", Orbit(satellite => satellite.YawSwingMs = 120_001),
            "satellites[0].yawSwingMs is 120001, and the range is 240 to 120000");

        yield return Refused("a phase past two minutes back", Orbit(satellite => satellite.PhaseMs = -120_001),
            "satellites[0].phaseMs is -120001, and the range is -120000 to 120000");

        yield return Refused("a still pose past a full circle", Orbit(satellite => satellite.StillDeg = 361d),
            "satellites[0].stillDeg is 361, and the range is 0 to 360");

        yield return Refused("an invisible figure", Orbit(satellite => satellite.Opacity = 0d),
            "satellites[0].opacity is 0, and the range is 0.05 to 1");

        yield return Refused("a figure lean past half a turn", Orbit(satellite =>
            {
                satellite.LeanDeg   = -181d;
                satellite.RadiusPct = 100d;
            }),
            "satellites[0].leanDeg is -181, and the range is -180 to 180");

        yield return Refused("a turn of neither kind", Orbit(satellite => satellite.Turn = "flip"),
            "satellites[0].turn is 'mirror' or 'spin'");

        yield return Refused("a figure shrunk to nothing up close", Orbit(satellite => satellite.NearScale = 0d),
            "satellites[0].nearScale is 0, and the range is 0.1 to 4");

        yield return Refused("a scale out of range, which is not also measured", Orbit(satellite => satellite.NearScale = 5d),
            "satellites[0].nearScale is 5, and the range is 0.1 to 4");

        yield return Refused("a figure grown past four times far away", Orbit(satellite => satellite.FarScale = 5d),
            "satellites[0].farScale is 5, and the range is 0.1 to 4");

        yield return Refused("a dimming past dark", Orbit(satellite => satellite.Dim = 1.5d),
            "satellites[0].dim is 1.5, and the range is 0 to 1");

        yield return Refused("a negative haze", Orbit(satellite => satellite.Haze = -0.5d),
            "satellites[0].haze is -0.5, and the range is 0 to 1");

        yield return Refused("a blur past an eighth of the face", Orbit(satellite => satellite.BlurPct = 13d),
            "satellites[0].blurPct is 13, and the range is 0 to 12");

        yield return Refused("a negative bob", Orbit(satellite => satellite.BobPct = -1d),
            "satellites[0].bobPct is -1, and the range is 0 to 50");

        yield return Refused("a bob too quick", Orbit(satellite => satellite.BobPeriodMs = 239),
            "satellites[0].bobPeriodMs is 239, and the range is 240 to 120000");

        // The strip a figure is drawn from.
        AvatarOrbitPayload Strip(int? frames, int? columns, Action<CosmeticOrbitSprite>? change = null)
            => Orbit(satellite =>
            {
                satellite.Sprite = new CosmeticOrbitSprite { Frames = frames, Columns = columns };
                change?.Invoke(satellite.Sprite);
            });

        yield return Refused("a strip with no frames, which is checked no further", Strip(null, 4, sprite => sprite.Still = 9),
            "satellites[0].sprite.frames is missing");

        yield return Refused("a strip of one frame", Strip(1, 1), "satellites[0].sprite.frames is 1, and the range is 2 to 64");

        yield return Refused("a strip of sixty-five frames", Strip(65, 5), "satellites[0].sprite.frames is 65, and the range is 2 to 64");

        yield return Refused("a strip with no columns, which is checked no further", Strip(8, null, sprite => sprite.Still = 9),
            "satellites[0].sprite.columns is missing");

        yield return Refused("a strip of no columns", Strip(8, 0), "satellites[0].sprite.columns is 0, and the range is 1 to 64");

        yield return Refused("a strip played not at all", Strip(8, 4, sprite => sprite.Fps = 0), "satellites[0].sprite.fps is 0, and the range is 1 to 60");

        yield return Refused("a strip played too fast", Strip(8, 4, sprite => sprite.Fps = 61), "satellites[0].sprite.fps is 61, and the range is 1 to 60");

        yield return Refused("more columns than frames", Strip(2, 4), "satellites[0].sprite.frames must divide evenly into rows of 4");

        yield return Refused("a last row that runs out", Strip(6, 4), "satellites[0].sprite.frames must divide evenly into rows of 4");

        yield return Refused("a still frame past the strip", Strip(8, 4, sprite => sprite.Still = 8),
            "satellites[0].sprite.still names frame 8, and the strip has 8");

        yield return Refused("a still frame before the strip", Strip(8, 4, sprite => sprite.Still = -1),
            "satellites[0].sprite.still names frame -1, and the strip has 8");

        // Reach: what lands on the neighbouring message is the sum, so the sum is checked.
        yield return Refused("a figure that spins presents its corner", Orbit(satellite => satellite.Turn = "spin"),
            "satellites[0] reaches 167 per cent of the face past its edge, and 150 is as far as an ornament goes");

        yield return Refused("a figure that leans presents its corner", Orbit(satellite => satellite.LeanDeg = 10d),
            "satellites[0] reaches 167 per cent of the face past its edge, and 150 is as far as an ornament goes");

        yield return Refused("a figure that bobs reaches further", Orbit(satellite => satellite.BobPct = 10d),
            "satellites[0] reaches 160 per cent of the face past its edge, and 150 is as far as an ornament goes");

        yield return Refused("a figure larger up close reaches further", Orbit(satellite => satellite.NearScale = 1.5d),
            "satellites[0] reaches 250 per cent of the face past its edge, and 150 is as far as an ornament goes");

        yield return Refused("a figure larger far away reaches further", Orbit(satellite => satellite.FarScale = 1.25d),
            "satellites[0] reaches 200 per cent of the face past its edge, and 150 is as far as an ornament goes");

        yield return Refused("every satellite is measured, the second named by its place",
            Orbit(Satellite(), Satellite(satellite => satellite.Turn = "spin")),
            "satellites[1] reaches 167 per cent of the face past its edge, and 150 is as far as an ornament goes");
    }

    private static IEnumerable<TestCaseData> Acceptances()
    {
        yield return Accepted("a figure square to the screen at the ceiling", Orbit(Satellite()));
        yield return Accepted("eight satellites", Orbit(Enumerable.Range(0, 8).Select(_ => Satellite()).ToArray()));
        yield return Accepted("the largest still size", new AvatarOrbitPayload { Satellites = [Satellite()], MinSizePx = 512 });
        yield return Accepted("a figure that would spin but does not face its travel", Orbit(satellite =>
        {
            satellite.Turn       = "spin";
            satellite.FaceTravel = false;
        }));

        yield return Accepted("a figure that spins facing its travel, well inside", Orbit(satellite =>
        {
            satellite.Turn       = "spin";
            satellite.FaceTravel = true;
            satellite.RadiusPct  = 100d;
        }));

        yield return Accepted("a figure that mirrors", Orbit(satellite => satellite.Turn = "mirror"));
        yield return Accepted("a figure with an empty turn", Orbit(satellite => satellite.Turn = string.Empty));
        yield return Accepted("a figure with no lean", Orbit(satellite => satellite.LeanDeg = 0d));
        yield return Accepted("a figure smaller far away", Orbit(satellite => satellite.FarScale = 0.5d));
        yield return Accepted("every number at the edge of its range", Orbit(satellite =>
        {
            satellite.RadiusPct   = 0d;
            satellite.WidthPct    = 200d;
            satellite.HeightPct   = 1d;
            satellite.PeriodMs    = 240;
            satellite.TiltDeg     = 90d;
            satellite.YawDeg      = -180d;
            satellite.YawSwingDeg = 180d;
            satellite.YawSwingMs  = 120_000;
            satellite.PhaseMs     = 120_000;
            satellite.Reverse     = true;
            satellite.StillDeg    = 360d;
            satellite.Opacity     = 0.05d;
            satellite.Dim         = 1d;
            satellite.Haze        = 0d;
            satellite.BlurPct     = 12d;
            satellite.Occlude     = false;
            satellite.BobPct      = 0d;
            satellite.BobPeriodMs = 120_000;
            satellite.Sprite      = new CosmeticOrbitSprite { Frames = 64, Columns = 8, Fps = 60, Still = 63 };
        }));

        foreach (var slot in OrbitKind.Assets.Keys)
        {
            yield return Accepted($"the {slot} slot", Orbit(satellite => satellite.Slot = slot.ToString()));
        }
    }

    /// <summary>
    /// The payload's own list of slots is the kind's. A slot the kind gained would otherwise be one no
    /// figure may name, and one it lost a figure that names a file nobody can upload.
    /// </summary>
    [Test]
    public void A_figure_may_name_exactly_the_slots_the_kind_declares()
    {
        foreach (var slot in Enum.GetValues<CosmeticAssetSlot>())
        {
            var errors = ErrorsOf(Orbit(satellite => satellite.Slot = slot.ToString()));

            Assert.That(errors, OrbitKind.Assets.ContainsKey(slot) ? Is.Empty : Is.Not.Empty, slot.ToString());
        }
    }

    [Test]
    public void A_figure_arrives_with_every_number_the_row_said()
    {
        var orbit = new AvatarOrbitPayload
        {
            MinSizePx  = 34,
            Satellites =
            [
                Satellite(satellite =>
                {
                    satellite.Slot        = "Secondary";
                    satellite.Sprite      = new CosmeticOrbitSprite { Frames = 8, Columns = 4, Fps = 12, Still = 3 };
                    satellite.TiltDeg     = 70d;
                    satellite.YawDeg      = -20d;
                    satellite.YawSwingDeg = 15d;
                    satellite.YawSwingMs  = 9000;
                    satellite.PhaseMs     = -1500;
                    satellite.Reverse     = true;
                    satellite.StillDeg    = 90d;
                    satellite.Opacity     = 0.8d;
                    satellite.LeanDeg     = 5d;
                    satellite.FaceTravel  = true;
                    satellite.Turn        = "mirror";
                    satellite.NearScale   = 1.2d;
                    satellite.FarScale    = 0.6d;
                    satellite.Dim         = 0.3d;
                    satellite.Haze        = 0.2d;
                    satellite.BlurPct     = 1.5d;
                    satellite.Occlude     = false;
                    satellite.BobPct      = 4d;
                    satellite.BobPeriodMs = 1300;
                })
            ]
        };

        var wire = (PayloadAvatarOrbit)orbit.ToWire();

        Assert.Multiple(() =>
        {
            Assert.That(wire.minSizePx, Is.EqualTo((ushort)34));
            Assert.That(wire.satellites.Single(), Is.EqualTo(new OrbitSatellite(
                AssetSlot.Secondary,
                new OrbitSprite(8, 4, 12, 3),
                160d,
                70d,
                -20d,
                15d,
                9000,
                6000,
                -1500,
                true,
                90d,
                80d,
                80d,
                0.8d,
                5d,
                true,
                OrbitTurn.Mirror,
                1.2d,
                0.6d,
                0.3d,
                0.2d,
                1.5d,
                false,
                4d,
                1300)));
        });
    }

    [Test]
    public void A_figure_that_says_little_arrives_saying_little()
    {
        var wire = (PayloadAvatarOrbit)Orbit(Satellite(satellite => satellite.Sprite = new CosmeticOrbitSprite { Frames = 8, Columns = 4 })).ToWire();

        Assert.Multiple(() =>
        {
            Assert.That(wire.minSizePx, Is.Null);
            Assert.That(wire.satellites.Single(), Is.EqualTo(new OrbitSatellite(
                AssetSlot.Primary,
                new OrbitSprite(8, 4, null, null),
                160d,
                null,
                null,
                null,
                null,
                6000,
                null,
                null,
                null,
                80d,
                80d,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null)));
        });
    }

    [TestCase("mirror", OrbitTurn.Mirror)]
    [TestCase("spin", OrbitTurn.Spin)]
    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase("flip", null)]
    public void A_turn_is_its_case_or_absent(string? turn, OrbitTurn? expected)
    {
        var wire = (PayloadAvatarOrbit)Orbit(Satellite(satellite => satellite.Turn = turn)).ToWire();

        Assert.That(((OrbitSatellite)wire.satellites.Single()).turn, Is.EqualTo(expected));
    }

    [Test]
    public void A_figure_with_no_strip_is_one_drawing()
    {
        var wire = (PayloadAvatarOrbit)Orbit(Satellite()).ToWire();

        Assert.That(((OrbitSatellite)wire.satellites.Single()).sprite, Is.Null);
    }

    /// <summary>The mapping is only handed what passed validation; a body it has no case for stops it.</summary>
    [Test]
    public void An_unvalidated_body_of_no_known_type_stops_the_mapping()
        => Assert.That(() => Orbit(Satellite(satellite => satellite.Type = "moon")).ToWire(),
            Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains("an orbit body this build has no case for"));
}
