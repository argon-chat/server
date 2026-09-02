namespace ArgonSharedLogicTest.Moderation;

using Argon.Features.Moderation;
using System.Security.Cryptography;

/// <summary>
/// The hashes a report carries about who filed it: keyed, or absent.
/// </summary>
[TestFixture]
public class ReporterIdentityHasherTests
{
    private const string Pepper = "a-key-only-the-deployment-holds";

    [Test]
    public void Without_a_key_nothing_is_stored([Values(null, "", "   ")] string? pepper)
        => Assert.That(ReporterIdentityHasher.Hash(pepper, "203.0.113.7"), Is.Null);

    [Test]
    public void Without_a_value_nothing_is_stored([Values(null, "", "   ")] string? value)
        => Assert.That(ReporterIdentityHasher.Hash(Pepper, value), Is.Null);

    /// <summary>
    /// "unknown" is what the server reports for a request that did not come through a trusted
    /// edge. Hashing it would make every such reporter one person.
    /// </summary>
    [Test]
    public void The_placeholder_address_is_not_an_address([Values("unknown", "UNKNOWN", " unknown ")] string value)
        => Assert.That(ReporterIdentityHasher.Hash(Pepper, value), Is.Null);

    [Test]
    public void Equal_inputs_hash_equal_and_the_hash_is_lower_hex()
    {
        var hash = ReporterIdentityHasher.Hash(Pepper, "203.0.113.7");

        Assert.Multiple(() =>
        {
            Assert.That(hash, Is.EqualTo(ReporterIdentityHasher.Hash(Pepper, "203.0.113.7")));
            Assert.That(hash, Is.EqualTo(ReporterIdentityHasher.Hash(Pepper, " 203.0.113.7 ")), "surrounding whitespace is not identity");
            Assert.That(hash, Has.Length.EqualTo(64).And.Match("^[0-9a-f]+$"));
        });
    }

    [Test]
    public void Different_inputs_or_keys_hash_differently()
    {
        var hash = ReporterIdentityHasher.Hash(Pepper, "203.0.113.7");

        Assert.Multiple(() =>
        {
            Assert.That(ReporterIdentityHasher.Hash(Pepper, "203.0.113.8"), Is.Not.EqualTo(hash));
            Assert.That(ReporterIdentityHasher.Hash("another-key-entirely-here", "203.0.113.7"), Is.Not.EqualTo(hash),
                "the same address under another deployment's key is a different value — the table is worthless without the key");
        });
    }

    /// <summary>The whole reason for the key: a bare digest of an IPv4 address is a lookup away from the address.</summary>
    [Test]
    public void The_hash_is_not_the_bare_digest_of_the_value()
        => Assert.That(ReporterIdentityHasher.Hash(Pepper, "203.0.113.7"),
            Is.Not.EqualTo(Convert.ToHexStringLower(SHA256.HashData("203.0.113.7"u8.ToArray()))));
}
