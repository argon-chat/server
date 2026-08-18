namespace ArgonSharedLogicTest;

using Argon.Features.Auth;
using Argon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Passwords stored under one scheme, read under another.
/// </summary>
/// <remarks>
/// The store already holds unsalted SHA-256 digests, so the change had to be one that lets those
/// accounts in and then quietly replaces what they are stored as. Both halves matter and neither is
/// visible from the other: a service that verified the old scheme but never reported it stale would
/// migrate nobody, and one that reported everything stale would rewrite a digest on every login.
/// </remarks>
[TestFixture]
public class PasswordHashingTests
{
    private static PasswordHashingService Service(
        PasswordHashAlgorithm algorithm = PasswordHashAlgorithm.Pbkdf2HmacSha512,
        int iterations = 10_000)
        => new(Options.Create(new PasswordHashingOptions
        {
            Algorithm  = algorithm,
            Iterations = iterations
        }), NullLogger<IPasswordHashingService>.Instance);

    /// <summary>How the digests already in the database were made.</summary>
    private static string LegacyDigest(string password)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    [Test]
    public void A_password_verifies_against_its_own_digest()
    {
        var service = Service();
        var digest  = service.HashPassword("correct horse battery staple");

        Assert.Multiple(() =>
        {
            Assert.That(service.ValidatePassword("correct horse battery staple", digest), Is.True);
            Assert.That(service.ValidatePassword("Correct horse battery staple", digest), Is.False);
            Assert.That(service.ValidatePassword("", digest), Is.False);
        });
    }

    /// <summary>
    /// The salt is what makes two identical passwords indistinguishable in the store, and it is the
    /// single thing the old scheme did not have.
    /// </summary>
    [Test]
    public void The_same_password_hashes_differently_every_time()
    {
        var service = Service();

        var first  = service.HashPassword("same password");
        var second = service.HashPassword("same password");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(service.ValidatePassword("same password", first), Is.True);
            Assert.That(service.ValidatePassword("same password", second), Is.True);
        });
    }

    [Test]
    public void A_digest_says_which_scheme_made_it()
        => Assert.That(Service().HashPassword("whatever"), Does.StartWith("$pbkdf2-sha512$i=10000$"));

    [Test]
    public void An_old_unsalted_digest_still_lets_its_owner_in()
    {
        var service = Service();

        Assert.Multiple(() =>
        {
            Assert.That(service.ValidatePassword("hunter2", LegacyDigest("hunter2")), Is.True);
            Assert.That(service.ValidatePassword("hunter3", LegacyDigest("hunter2")), Is.False);
        });
    }

    [Test]
    public void An_old_digest_is_reported_stale_so_the_login_can_replace_it()
        => Assert.That(Service().NeedsRehash(LegacyDigest("hunter2")), Is.True);

    [Test]
    public void A_current_digest_is_left_alone()
    {
        var service = Service();

        Assert.That(service.NeedsRehash(service.HashPassword("current")), Is.False);
    }

    /// <summary>
    /// Raising the iteration count is how this keeps up with hardware, and it only means anything if
    /// digests written under the old count are noticed.
    /// </summary>
    [Test]
    public void A_digest_from_a_weaker_setting_is_reported_stale()
    {
        var weak   = Service(iterations: 10_000).HashPassword("carry me over");
        var harder = Service(iterations: 20_000);

        Assert.Multiple(() =>
        {
            Assert.That(harder.NeedsRehash(weak), Is.True, "fewer iterations than configured");
            Assert.That(harder.ValidatePassword("carry me over", weak), Is.True,
                "and it still has to verify, or raising the count would lock everyone out");
        });
    }

    [Test]
    public void A_digest_from_another_algorithm_is_reported_stale_and_still_verifies()
    {
        var sha256 = Service(PasswordHashAlgorithm.Pbkdf2HmacSha256).HashPassword("moving house");
        var sha512 = Service(PasswordHashAlgorithm.Pbkdf2HmacSha512);

        Assert.Multiple(() =>
        {
            Assert.That(sha256, Does.StartWith("$pbkdf2-sha256$"));
            Assert.That(sha512.NeedsRehash(sha256), Is.True);
            Assert.That(sha512.ValidatePassword("moving house", sha256), Is.True);
        });
    }

    /// <summary>
    /// A digest is attacker-influenced input the moment a database is: nothing in it may decide how
    /// much memory reading it takes, and nothing malformed may throw where a false would do.
    /// </summary>
    [TestCase("")]
    [TestCase("$")]
    [TestCase("$pbkdf2-sha512")]
    [TestCase("$pbkdf2-sha512$i=1000")]
    [TestCase("$pbkdf2-sha512$i=1000$notbase64$notbase64")]
    [TestCase("$pbkdf2-sha512$i=0$AAAA$AAAA")]
    [TestCase("$pbkdf2-sha512$i=-1$AAAA$AAAA")]
    [TestCase("$scrypt$i=1000$AAAA$AAAA")]
    [TestCase("$pbkdf2-sha512$rounds=1000$AAAA$AAAA")]
    [TestCase("not a digest at all")]
    public void A_malformed_digest_is_refused_rather_than_thrown_on(string digest)
    {
        var service = Service();

        Assert.Multiple(() =>
        {
            Assert.That(service.ValidatePassword("anything", digest), Is.False);
            Assert.That(() => service.NeedsRehash(digest), Throws.Nothing);
        });
    }

    /// <summary>
    /// The buffers are on the stack, so an over-long password must be turned away rather than
    /// allowed to size them.
    /// </summary>
    [Test]
    public void An_absurdly_long_password_is_refused_rather_than_sized_into_the_stack()
    {
        var service = Service();

        Assert.That(service.HashPassword(new string('x', 2048)), Is.Null);
    }
}
