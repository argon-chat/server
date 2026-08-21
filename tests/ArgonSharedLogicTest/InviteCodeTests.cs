namespace ArgonSharedLogicTest;

using Argon.Entities;

/// <summary>
/// Invite codes are stored as a <c>ulong</c> primary key but travel as a nine-character base-62
/// string, usually in the dashed display form users copy out of the client. Every join goes through
/// this encode/decode pair, so an asymmetry here silently turns every shared link into NOT_FOUND.
/// </summary>
[TestFixture]
public class InviteCodeTests
{
    [Test]
    public void DecodeFromUlong_ProducesADashedNineCharacterCode()
    {
        var code = InviteCodeEntityData.DecodeFromUlong(123456789UL);

        Assert.Multiple(() =>
        {
            Assert.That(code, Has.Length.EqualTo(11), "9 characters plus two separators");
            Assert.That(code[3], Is.EqualTo('-'));
            Assert.That(code[7], Is.EqualTo('-'));
        });
    }

    [Test]
    public void EncodeThenDecode_RoundTrips([Values(0UL, 1UL, 62UL, 123456789UL, 987654321012UL)] ulong id)
    {
        var code    = InviteCodeEntityData.DecodeFromUlong(id);
        var decoded = InviteCodeEntityData.EncodeToUlong(code);

        Assert.That(decoded, Is.EqualTo(id));
    }

    [Test]
    public void EncodeToUlong_IgnoresSeparators()
    {
        var dashed = InviteCodeEntityData.DecodeFromUlong(4242424242UL);
        var plain  = InviteCodeEntityData.RemoveSeparators(dashed);

        Assert.That(InviteCodeEntityData.EncodeToUlong(plain), Is.EqualTo(InviteCodeEntityData.EncodeToUlong(dashed)));
    }

    [Test]
    public void EncodeToUlong_RejectsCharactersOutsideTheAlphabet()
        => Assert.Throws<ArgumentException>(() => InviteCodeEntityData.EncodeToUlong("ABC!EFGHI"));

    [Test]
    public void TryParseInviteCode_AcceptsTheDashedDisplayForm()
    {
        var dashed = InviteCodeEntityData.DecodeFromUlong(555_000_111UL);

        Assert.Multiple(() =>
        {
            Assert.That(InviteCodeEntityData.TryParseInviteCode(dashed, out var id), Is.True);
            Assert.That(id, Is.EqualTo(555_000_111UL));
        });
    }

    [Test]
    public void TryParseInviteCode_AcceptsTheUndashedForm()
    {
        var plain = InviteCodeEntityData.RemoveSeparators(InviteCodeEntityData.DecodeFromUlong(777UL));

        Assert.Multiple(() =>
        {
            Assert.That(InviteCodeEntityData.TryParseInviteCode(plain, out var id), Is.True);
            Assert.That(id, Is.EqualTo(777UL));
        });
    }

    [Test]
    public void TryParseInviteCode_RejectsGarbage(
        [Values("", "   ", "short", "way-too-long-to-be-an-invite", "ABC!EFGHI")] string input)
    {
        Assert.Multiple(() =>
        {
            Assert.That(InviteCodeEntityData.TryParseInviteCode(input, out var id), Is.False);
            Assert.That(id, Is.Null);
        });
    }

    [Test]
    public void GenerateInviteCode_IsNineCharactersOfTheAlphabet()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        for (var i = 0; i < 50; i++)
        {
            var code = InviteCodeEntityData.GenerateInviteCode();

            Assert.That(code, Has.Length.EqualTo(9));
            Assert.That(code.All(alphabet.Contains), Is.True, $"'{code}' contains a character outside the alphabet");
        }
    }

    [Test]
    public void GenerateInviteCode_IsParsable()
    {
        // Generation and parsing share an alphabet but not an implementation; a divergence would
        // only surface when someone tried to use a freshly minted code.
        for (var i = 0; i < 50; i++)
        {
            var code = InviteCodeEntityData.GenerateInviteCode();
            Assert.That(InviteCodeEntityData.TryParseInviteCode(code, out _), Is.True, $"'{code}' failed to parse");
        }
    }

    [Test]
    public void HasExpired_ComparesAgainstNow()
    {
        var expired = new InviteCodeEntityData(
            new InviteCode("ABC-DEF-GHI"), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-1), 0, 10, DateTimeOffset.UtcNow.AddHours(-2));

        var live = expired with { expireTime = DateTimeOffset.UtcNow.AddMinutes(10) };

        Assert.Multiple(() =>
        {
            Assert.That(expired.HasExpired(), Is.True);
            Assert.That(live.HasExpired(), Is.False);
        });
    }
}
