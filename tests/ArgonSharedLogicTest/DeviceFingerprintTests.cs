namespace ArgonSharedLogicTest;

using Argon.Features.Auth;

/// <summary>
/// Deciding whether two logins came from the same machine.
/// </summary>
/// <remarks>
/// <para>The scheme this replaces folded every hardware signal into one string and compared it for
/// equality. That fails in both directions at once: swapping a disk turned a returning user into a
/// stranger, and — because the Windows half of it was the motherboard serial plus <c>ProcessorId</c>
/// — two different people with the same laptop model could produce the same id. The second case is
/// the dangerous one, since the feature it feeds is hardware banning.</para>
///
/// <para>So the tests worth having are about the edges of the score, not the happy path: what must
/// <em>not</em> reach the threshold, and what must survive an ordinary upgrade.</para>
/// </remarks>
[TestFixture]
public class DeviceFingerprintTests
{
    private static DeviceFingerprint From(params (string code, string value)[] components)
        => DeviceFingerprint.Parse(
            $"{DeviceFingerprint.CurrentVersion};" + string.Join(",", components.Select(c => $"{c.code}:{c.value}")));

    [Test]
    public void SameMachineTwice_Matches()
    {
        var machine = From(("mg", "a1"), ("su", "b2"), ("ds", "c3"), ("mac", "d4"), ("mb", "e5"), ("cpu", "f6"));

        Assert.That(machine.ScoreAgainst(machine), Is.EqualTo(DeviceFingerprint.MaxScore - 5),
            "every signal but the volume serial was reported");
        Assert.That(machine.IsSameMachineAs(machine), Is.True);
    }

    [Test]
    public void TwoStrangersOnTheSameLaptopModel_DoNotMatch()
    {
        // The exact collision the old single-hash scheme produced: identical board model reporting
        // the same placeholder serial, identical CPU model, nothing else in common.
        var alice = From(("mg", "alice-install"), ("su", "alice-board"), ("mb", "shared"), ("cpu", "same-model"));
        var bob   = From(("mg", "bob-install"),   ("su", "bob-board"),   ("mb", "shared"), ("cpu", "same-model"));

        Assert.That(alice.ScoreAgainst(bob), Is.EqualTo(10));
        Assert.That(alice.IsSameMachineAs(bob), Is.False, "a shared board model is not a shared machine");
    }

    [Test]
    public void TheWeakSignalsTogether_CannotReachTheThreshold()
    {
        // Stated as a property rather than an example: if this ever stops holding, hardware bans
        // start hitting bystanders who merely bought the same computer.
        var weak = DeviceFingerprint.Signals
           .Where(s => s.Code is "mb" or "cpu" or "vs")
           .Sum(s => s.Weight);

        Assert.That(weak, Is.LessThan(DeviceFingerprint.SameMachineThreshold));
    }

    [Test]
    public void ReachingTheThreshold_NeedsAStrongSignal()
    {
        var withoutStrong = DeviceFingerprint.Signals
           .Where(s => s.Code is not "mg" and not "su")
           .Sum(s => s.Weight);

        // Everything except the two per-machine signals, all agreeing, must still fall short.
        Assert.That(withoutStrong, Is.LessThan(DeviceFingerprint.SameMachineThreshold));
    }

    [Test]
    public void ADiskSwap_StillLooksLikeTheSameMachine()
    {
        var before = From(("mg", "a1"), ("su", "b2"), ("ds", "old-disk"), ("vs", "old-vol"), ("mac", "d4"));
        var after  = From(("mg", "a1"), ("su", "b2"), ("ds", "new-disk"), ("vs", "new-vol"), ("mac", "d4"));

        // The point of scoring rather than comparing: an ordinary upgrade must not sign someone out
        // of their account or read as a new person evading something.
        Assert.That(after.IsSameMachineAs(before), Is.True);
    }

    [Test]
    public void AnOsReinstall_IsStillTheSameMachine()
    {
        // MachineGuid is regenerated, the board is not.
        var before = From(("mg", "install-one"), ("su", "b2"), ("ds", "c3"), ("mac", "d4"), ("mb", "e5"));
        var after  = From(("mg", "install-two"), ("su", "b2"), ("ds", "c3"), ("mac", "d4"), ("mb", "e5"));

        Assert.That(after.IsSameMachineAs(before), Is.True);
    }

    [Test]
    public void AMissingSignalOnBothSides_IsNotAgreement()
    {
        var one = From(("mg", "one"), ("cpu", "same-model"));
        var two = From(("mg", "two"), ("cpu", "same-model"));

        // Neither reported a board serial. Two absences must not add up to a match, or every
        // machine that cannot read a given signal becomes the same machine.
        Assert.That(one.ScoreAgainst(two), Is.EqualTo(2));
    }

    [Test]
    public void PlaceholderSerials_AreDroppedRatherThanMatched()
    {
        // What OEM firmware actually ships instead of a serial. Every unit from that vendor agrees
        // on it, so counting it would manufacture matches out of nothing.
        var one = From(("mg", "one"), ("mb", "Default string"), ("su", "To be filled by O.E.M."));
        var two = From(("mg", "two"), ("mb", "Default string"), ("su", "To be filled by O.E.M."));

        Assert.Multiple(() =>
        {
            Assert.That(one.Components.ContainsKey("mb"), Is.False);
            Assert.That(one.Components.ContainsKey("su"), Is.False);
            Assert.That(one.ScoreAgainst(two), Is.Zero);
        });
    }

    [Test]
    public void AnAllZeroUuid_IsJunk()
    {
        var fingerprint = From(("su", "00000000-0000-0000-0000-000000000000"), ("mg", "real"));

        Assert.That(fingerprint.Components.Keys, Is.EquivalentTo(new[] { "mg" }));
    }

    [Test]
    public void AnUnknownComponentCode_IsIgnored()
    {
        var fingerprint = From(("mg", "real"), ("gpu", "something"));

        // A component with no weight cannot affect a score; keeping it would only invite it to be
        // given one later by accident.
        Assert.That(fingerprint.Components.Keys, Is.EquivalentTo(new[] { "mg" }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("1;")]
    [TestCase("nope;mg:a")]
    [TestCase("2;mg:a")]
    public void UnreadableInput_YieldsNothingRatherThanThrowing(string? raw)
    {
        // This parses on the auth path for every caller, old clients included. A malformed
        // fingerprint means we learn nothing about the device; it must never mean a failed login.
        var fingerprint = DeviceFingerprint.Parse(raw);

        Assert.That(fingerprint.IsEmpty, Is.True);
    }

    [Test]
    public void AnEmptyFingerprint_MatchesNothing()
    {
        var real = From(("mg", "a1"), ("su", "b2"), ("ds", "c3"));

        Assert.Multiple(() =>
        {
            // Including itself: two clients that reported nothing are not thereby the same machine.
            Assert.That(DeviceFingerprint.Empty.IsSameMachineAs(real), Is.False);
            Assert.That(DeviceFingerprint.Empty.IsSameMachineAs(DeviceFingerprint.Empty), Is.False);
            Assert.That(real.IsSameMachineAs(DeviceFingerprint.Empty), Is.False);
        });
    }

    [Test]
    public void ScoringIsSymmetric()
    {
        var one = From(("mg", "a1"), ("su", "b2"), ("mb", "x"));
        var two = From(("mg", "a1"), ("ds", "c3"), ("mb", "x"));

        Assert.That(one.ScoreAgainst(two), Is.EqualTo(two.ScoreAgainst(one)));
    }

    [Test]
    public void ComponentValuesAreCaseSensitive()
    {
        var one = From(("mg", "AbC"));
        var two = From(("mg", "abc"));

        // They are hex digests off the client, so a case difference is a different value and not a
        // formatting variation to be forgiven.
        Assert.That(one.ScoreAgainst(two), Is.Zero);
    }
}
