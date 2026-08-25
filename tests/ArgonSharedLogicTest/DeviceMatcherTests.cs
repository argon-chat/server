namespace ArgonSharedLogicTest;

using Argon.Features.Auth;
using Microsoft.Extensions.Options;

/// <summary>
/// Deciding whether two logins came from the same machine.
/// </summary>
/// <remarks>
/// <para>The scheme this replaces folded every hardware signal into one string and compared it for
/// equality. That fails in both directions at once: swapping a disk turned a returning user into a
/// stranger, and — because the Windows half of it was the motherboard serial plus the CPU model —
/// two different people with the same laptop could produce the same id. The second case is the
/// dangerous one, since the feature it feeds is hardware banning.</para>
///
/// <para>So the tests worth having are about the edges of the score, not the happy path: what must
/// <em>not</em> reach the threshold, and what must survive an ordinary upgrade.</para>
///
/// <para><b>The table below is invented for this fixture</b> — invented codes, invented weights, and
/// no relation to what any deployment runs. The shipped table is configuration exactly so that it is
/// not written down in a public repository, and a fixture that pasted it back in would undo that as
/// thoroughly as leaving it in the source did. What is demonstrated here is therefore the behaviour
/// of the scoring, on a table where the roles are named rather than real; that a table someone
/// actually deployed still has these properties is checked separately, by the rules in
/// <see cref="DeviceMatchingOptions"/> and <see cref="DeviceMatchingRulesTests"/>.</para>
/// </remarks>
[TestFixture]
public class DeviceMatcherTests
{
    // Signals by the role they play rather than by what they read, since the roles are the whole
    // content of these tests: two that pin down one physical machine, two that are replaced when a
    // part is, and two that every unit of the model agrees on.
    private const string PerInstall = "alpha";
    private const string PerUnit    = "beta";
    private const string PartSerial = "gamma";
    private const string Adapters   = "delta";
    private const string ModelWide  = "epsilon";
    private const string DiesOnWipe = "zeta";

    private const int Threshold = 150;

    private static DeviceMatcher Matcher()
        => new(Options.Create(new DeviceMatchingOptions
        {
            Weights = new Dictionary<string, int>
            {
                [PerInstall] = 100,
                [PerUnit]    = 100,
                [PartSerial] = 25,
                [Adapters]   = 20,
                [ModelWide]  = 10,
                [DiesOnWipe] = 5
            },
            SameMachineThreshold = Threshold
        }));

    private static DeviceFingerprint From(DeviceMatcher matcher, params (string Code, string Value)[] components)
        => matcher.Parse($"{DeviceFingerprint.CurrentVersion};" +
                         string.Join(",", components.Select(c => $"{c.Code}:{c.Value}")));

    [Test]
    public void SameMachineTwice_Matches()
    {
        var matcher = Matcher();
        var machine = From(matcher,
            (PerInstall, "a1"), (PerUnit, "b2"), (PartSerial, "c3"), (Adapters, "d4"), (ModelWide, "e5"));

        Assert.Multiple(() =>
        {
            Assert.That(matcher.Score(machine, machine), Is.EqualTo(matcher.MaxScore - 5),
                "every signal but the one that dies on a wipe was reported");
            Assert.That(matcher.IsSameMachine(machine, machine), Is.True);
        });
    }

    [Test]
    public void TwoStrangersOnTheSameLaptopModel_DoNotMatch()
    {
        // The exact collision the old single-hash scheme produced: two machines agreeing only on
        // the values every unit of that model agrees on, and on nothing that identifies a unit.
        var matcher = Matcher();
        var alice   = From(matcher, (PerInstall, "alice-install"), (PerUnit, "alice-board"), (ModelWide, "shared"), (DiesOnWipe, "shared"));
        var bob     = From(matcher, (PerInstall, "bob-install"), (PerUnit, "bob-board"), (ModelWide, "shared"), (DiesOnWipe, "shared"));

        Assert.Multiple(() =>
        {
            Assert.That(matcher.Score(alice, bob), Is.EqualTo(15));
            Assert.That(matcher.IsSameMachine(alice, bob), Is.False, "a shared model is not a shared machine");
        });
    }

    [Test]
    public void ADiskSwap_StillLooksLikeTheSameMachine()
    {
        var matcher = Matcher();
        var before  = From(matcher, (PerInstall, "a1"), (PerUnit, "b2"), (PartSerial, "old"), (DiesOnWipe, "old"), (Adapters, "d4"));
        var after   = From(matcher, (PerInstall, "a1"), (PerUnit, "b2"), (PartSerial, "new"), (DiesOnWipe, "new"), (Adapters, "d4"));

        // The point of scoring rather than comparing: an ordinary upgrade must not sign someone out
        // of their account or read as a new person evading something.
        Assert.That(matcher.IsSameMachine(after, before), Is.True);
    }

    [Test]
    public void AnOsReinstall_IsStillTheSameMachine()
    {
        // The per-installation value is regenerated; the unit it was installed on is not.
        var matcher = Matcher();
        var before  = From(matcher, (PerInstall, "install-one"), (PerUnit, "b2"), (PartSerial, "c3"), (Adapters, "d4"), (ModelWide, "e5"));
        var after   = From(matcher, (PerInstall, "install-two"), (PerUnit, "b2"), (PartSerial, "c3"), (Adapters, "d4"), (ModelWide, "e5"));

        Assert.That(matcher.IsSameMachine(after, before), Is.True);
    }

    [Test]
    public void AMissingSignalOnBothSides_IsNotAgreement()
    {
        var matcher = Matcher();
        var one     = From(matcher, (PerInstall, "one"), (ModelWide, "same-model"));
        var two     = From(matcher, (PerInstall, "two"), (ModelWide, "same-model"));

        // Neither reported a unit serial. Two absences must not add up to a match, or every machine
        // that cannot read a given signal becomes the same machine.
        Assert.That(matcher.Score(one, two), Is.EqualTo(10));
    }

    [Test]
    public void PlaceholderSerials_AreDroppedRatherThanMatched()
    {
        // What OEM firmware actually ships instead of a serial. Every unit from that vendor agrees
        // on it, so counting it would manufacture matches out of nothing.
        var matcher = Matcher();
        var one     = From(matcher, (PerInstall, "one"), (PerUnit, "Default string"), (PartSerial, "To be filled by O.E.M."));
        var two     = From(matcher, (PerInstall, "two"), (PerUnit, "Default string"), (PartSerial, "To be filled by O.E.M."));

        Assert.Multiple(() =>
        {
            Assert.That(one.Components.ContainsKey(PerUnit), Is.False);
            Assert.That(one.Components.ContainsKey(PartSerial), Is.False);
            Assert.That(matcher.Score(one, two), Is.Zero);
        });
    }

    [Test]
    public void AnAllZeroUuid_IsJunk()
    {
        var matcher     = Matcher();
        var fingerprint = From(matcher, (PerUnit, "00000000-0000-0000-0000-000000000000"), (PerInstall, "real"));

        Assert.That(fingerprint.Components.Keys, Is.EquivalentTo(new[] { PerInstall }));
    }

    [Test]
    public void AComponentWithNoWeight_IsNotEvenParsed()
    {
        var matcher     = Matcher();
        var fingerprint = From(matcher, (PerInstall, "real"), ("omega", "something"));

        // The weight table is the only list of codes there is, so an unweighted component cannot
        // affect a score — and storing it would only invite it to be given one later by accident.
        Assert.That(fingerprint.Components.Keys, Is.EquivalentTo(new[] { PerInstall }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("1;")]
    [TestCase("nope;alpha:a")]
    [TestCase("2;alpha:a")]
    public void UnreadableInput_YieldsNothingRatherThanThrowing(string? raw)
    {
        // This parses on the auth path for every caller, old clients included. A malformed
        // fingerprint means we learn nothing about the device; it must never mean a failed login.
        Assert.That(Matcher().Parse(raw).IsEmpty, Is.True);
    }

    [Test]
    public void AnEmptyFingerprint_MatchesNothing()
    {
        var matcher = Matcher();
        var real    = From(matcher, (PerInstall, "a1"), (PerUnit, "b2"), (PartSerial, "c3"));

        Assert.Multiple(() =>
        {
            // Including itself: two clients that reported nothing are not thereby the same machine.
            Assert.That(matcher.IsSameMachine(DeviceFingerprint.Empty, real), Is.False);
            Assert.That(matcher.IsSameMachine(DeviceFingerprint.Empty, DeviceFingerprint.Empty), Is.False);
            Assert.That(matcher.IsSameMachine(real, DeviceFingerprint.Empty), Is.False);
        });
    }

    [Test]
    public void ScoringIsSymmetric()
    {
        var matcher = Matcher();
        var one     = From(matcher, (PerInstall, "a1"), (PerUnit, "b2"), (ModelWide, "x"));
        var two     = From(matcher, (PerInstall, "a1"), (PartSerial, "c3"), (ModelWide, "x"));

        Assert.That(matcher.Score(one, two), Is.EqualTo(matcher.Score(two, one)));
    }

    [Test]
    public void ComponentValuesAreCaseSensitive()
    {
        var matcher = Matcher();
        var one     = From(matcher, (PerInstall, "AbC"));
        var two     = From(matcher, (PerInstall, "abc"));

        // They are hex digests off the client, so a case difference is a different value and not a
        // formatting variation to be forgiven.
        Assert.That(matcher.Score(one, two), Is.Zero);
    }

    /// <summary>
    /// The unconfigured deployment: no weights, therefore no device identity at all.
    /// </summary>
    /// <remarks>
    /// It has to read as "the client reported nothing" rather than as an error, because that is the
    /// state every caller on the auth path already handles — a client too old to send the field.
    /// </remarks>
    [Test]
    public void WithoutAConfiguredTable_NothingIsReadAndNothingMatches()
    {
        var unconfigured = new DeviceMatcher(Options.Create(new DeviceMatchingOptions()));

        Assert.Multiple(() =>
        {
            Assert.That(unconfigured.Enabled, Is.False);
            Assert.That(unconfigured.Parse($"{DeviceFingerprint.CurrentVersion};alpha:a1,beta:b2").IsEmpty, Is.True);
            Assert.That(unconfigured.IsSameMachine(DeviceFingerprint.Empty, DeviceFingerprint.Empty), Is.False);
        });
    }

    /// <summary>Round-trips through the form the observation table stores.</summary>
    [Test]
    public void ASerializedFingerprint_ReadsBackAsItself()
    {
        var matcher     = Matcher();
        var fingerprint = From(matcher, (PerUnit, "b2"), (PerInstall, "a1"), (PartSerial, "c3"));

        Assert.That(matcher.Parse(matcher.Serialize(fingerprint)).Components,
            Is.EqualTo(fingerprint.Components));
    }
}
