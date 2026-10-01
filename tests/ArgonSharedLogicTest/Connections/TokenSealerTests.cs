namespace ArgonSharedLogicTest.Connections;

using Argon.Features.Integrations.Connections;
using ArgonContracts;
using Microsoft.Extensions.Options;
using static ConnectionsTestOptions;

/// <summary>
/// Tokens at rest: a blob opens only on the row it was sealed for, under a key the sealer knows,
/// and says when that key is retired so the row can be re-sealed.
/// </summary>
[TestFixture]
public class TokenSealerTests
{
    private static readonly ProviderToken Token =
        new("access-token", "refresh-token", DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), "user-read-private");

    private static TokenSealer Sealer(ConnectionsOptions options) => new(Options.Create(options));

    [Test]
    public void A_token_comes_back_as_it_went_in()
    {
        var sealer = Sealer(Default());
        var id     = Guid.NewGuid();

        var blob = sealer.Seal(Token, id, ConnectionProvider.SPOTIFY);

        Assert.That(sealer.TryUnseal(blob, id, ConnectionProvider.SPOTIFY, out var opened, out var stale), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(opened, Is.EqualTo(Token));
            Assert.That(stale, Is.False);
            Assert.That(blob[0], Is.EqualTo(1), "the version byte");
            Assert.That(sealer.IsConfigured, Is.True);
        });
    }

    [Test]
    public void Two_seals_of_the_same_token_differ()
    {
        var sealer = Sealer(Default());
        var id     = Guid.NewGuid();

        Assert.That(sealer.Seal(Token, id, ConnectionProvider.GITHUB), Is.Not.EqualTo(sealer.Seal(Token, id, ConnectionProvider.GITHUB)),
            "a fresh nonce per write");
    }

    [Test]
    public void A_blob_moved_to_another_row_does_not_open()
    {
        var sealer = Sealer(Default());
        var id     = Guid.NewGuid();
        var blob   = sealer.Seal(Token, id, ConnectionProvider.GITHUB);

        Assert.Multiple(() =>
        {
            Assert.That(sealer.TryUnseal(blob, Guid.NewGuid(), ConnectionProvider.GITHUB, out _, out _), Is.False, "another connection");
            Assert.That(sealer.TryUnseal(blob, id, ConnectionProvider.SPOTIFY, out _, out _), Is.False, "another provider");
        });
    }

    [Test]
    public void A_damaged_blob_does_not_open()
    {
        var sealer = Sealer(Default());
        var id     = Guid.NewGuid();
        var blob   = sealer.Seal(Token, id, ConnectionProvider.GITHUB);

        blob[^1] ^= 0x01;

        Assert.Multiple(() =>
        {
            Assert.That(sealer.TryUnseal(blob, id, ConnectionProvider.GITHUB, out _, out _), Is.False);
            Assert.That(sealer.TryUnseal([1, 2, 3], id, ConnectionProvider.GITHUB, out _, out _), Is.False, "too short");
            Assert.That(sealer.TryUnseal(null, id, ConnectionProvider.GITHUB, out _, out _), Is.False);
        });
    }

    [Test]
    public void A_retired_key_still_opens_and_asks_for_a_reseal()
    {
        var key1 = Key();
        var key2 = Key();
        var id   = Guid.NewGuid();

        var first = Default(key1);
        first.TokenKeyVersion = 1;

        var rotated = Default(key2);
        rotated.TokenKeyVersion       = 2;
        rotated.RetiredTokenKeys["1"] = key1;

        var forgotten = Default(key2);
        forgotten.TokenKeyVersion = 2;

        var blob = Sealer(first).Seal(Token, id, ConnectionProvider.TWITCH);

        Assert.That(Sealer(rotated).TryUnseal(blob, id, ConnectionProvider.TWITCH, out var opened, out var stale), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(opened, Is.EqualTo(Token));
            Assert.That(stale, Is.True, "sealed under version 1 while version 2 is current");
            Assert.That(Sealer(rotated).Seal(Token, id, ConnectionProvider.TWITCH)[0], Is.EqualTo(2));
            Assert.That(Sealer(forgotten).TryUnseal(blob, id, ConnectionProvider.TWITCH, out _, out _), Is.False, "the old key is gone");
        });
    }

    [Test]
    public void Without_a_key_nothing_can_be_sealed()
    {
        var sealer = Sealer(Default(""));

        Assert.Multiple(() =>
        {
            Assert.That(sealer.IsConfigured, Is.False);
            Assert.That(() => sealer.Seal(Token, Guid.NewGuid(), ConnectionProvider.GITHUB), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void A_usable_key_is_exactly_thirty_two_bytes_of_base64()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TokenSealer.IsUsableKey(Key()), Is.True);
            Assert.That(TokenSealer.IsUsableKey(Convert.ToBase64String(new byte[16])), Is.False);
            Assert.That(TokenSealer.IsUsableKey("not base64!"), Is.False);
            Assert.That(TokenSealer.IsUsableKey(null), Is.False);
            Assert.That(TokenSealer.IsUsableKey(""), Is.False);
        });
    }
}
