namespace ArgonSharedLogicTest;

using Argon.Features.Auth;

using Argon.Entities;
using Argon.Shared;
using Argon.Services;
using ArgonContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

/// <summary>
/// Small pure helpers that a lot of the server leans on: password digests, the packed Argon id
/// layout, and the polymorphic message-entity JSON converter that persists rich text.
/// </summary>
[TestFixture]
public class PasswordHashingServiceTests
{
    private static PasswordHashingService NewService()
        => new(Options.Create(new PasswordHashingOptions { Iterations = 10_000 }),
            NullLogger<IPasswordHashingService>.Instance);

    /// <summary>
    /// Hashing the same password twice must <em>not</em> agree.
    /// </summary>
    /// <remarks>
    /// This assertion used to be the other way round, because the digest used to be a bare SHA-256 of
    /// the password and nothing else. That is precisely what made the store worth attacking: equal
    /// digests told you which accounts shared a password, and one table covered all of them. The salt
    /// is what removed that, and its whole visible effect is this inequality.
    /// </remarks>
    [Test]
    public void HashPassword_OfTheSamePassword_DiffersEachTime()
    {
        var service = NewService();

        Assert.That(service.HashPassword("correct horse battery staple"),
            Is.Not.EqualTo(service.HashPassword("correct horse battery staple")));
    }

    [Test]
    public void HashPassword_DiffersForDifferentInputs()
    {
        var service = NewService();

        Assert.That(service.HashPassword("password-a"), Is.Not.EqualTo(service.HashPassword("password-b")));
    }

    [Test]
    public void ValidatePassword_IsCaseSensitive()
    {
        var service = NewService();

        Assert.That(service.ValidatePassword("secret", service.HashPassword("Secret")), Is.False);
    }

    [Test]
    public void HashPassword_HandlesNonAsciiWithoutTruncating()
    {
        // The implementation stack-allocates by UTF-8 byte count rather than char count; a
        // multi-byte password must not be silently clipped.
        var service = NewService();

        var cyrillic = service.HashPassword("пароль-пароль");
        var emoji    = service.HashPassword("🔐🔐🔐");

        Assert.Multiple(() =>
        {
            Assert.That(cyrillic, Is.Not.Null.And.Not.Empty);
            Assert.That(emoji, Is.Not.Null.And.Not.Empty);
            Assert.That(service.ValidatePassword("пароль-пароль", cyrillic), Is.True);
            Assert.That(service.ValidatePassword("🔐🔐🔐", emoji), Is.True);
            Assert.That(service.ValidatePassword("🔐🔐🔐", cyrillic), Is.False);
        });
    }

    [Test]
    public void HashPassword_OfNull_IsNull()
        => Assert.That(NewService().HashPassword(null), Is.Null);

    [Test]
    public void ValidatePassword_MatchesItsOwnDigest()
    {
        var service = NewService();
        var digest  = service.HashPassword("s3cret!");

        Assert.Multiple(() =>
        {
            Assert.That(service.ValidatePassword("s3cret!", digest), Is.True);
            Assert.That(service.ValidatePassword("wrong", digest), Is.False);
        });
    }

    [Test]
    public void ValidatePassword_WithANullSideIsAlwaysFalse()
    {
        // Notably: a user with no digest (passkey-only, or the seeded system user) must never be
        // authenticable by supplying a null password.
        var service = NewService();

        Assert.Multiple(() =>
        {
            Assert.That(service.ValidatePassword(null, "digest"), Is.False);
            Assert.That(service.ValidatePassword("password", null), Is.False);
            Assert.That(service.ValidatePassword(null, null), Is.False);
        });
    }

    [Test]
    public void VerifyPassword_ReadsTheUsersDigest()
    {
        var service = NewService();
        var user = new UserEntity
        {
            Username       = "tester",
            DisplayName    = "Tester",
            Email          = "tester@test.local",
            PasswordDigest = service.HashPassword("hunter2")
        };

        Assert.Multiple(() =>
        {
            Assert.That(service.VerifyPassword("hunter2", user), Is.True);
            Assert.That(service.VerifyPassword("hunter3", user), Is.False);
        });
    }

    [Test]
    public void VerifyOtp_ComparesExactly()
    {
        var service = NewService();

        Assert.Multiple(() =>
        {
            Assert.That(service.VerifyOtp("123456", "123456"), Is.True);
            Assert.That(service.VerifyOtp("123456", "654321"), Is.False);
            Assert.That(service.VerifyOtp(null, "123456"), Is.False);
            Assert.That(service.VerifyOtp("123456", null), Is.False);
        });
    }
}

/// <summary>
/// Argon packs identity, region and a checksum into the 16 bytes of a <see cref="Guid"/>. Getting
/// the layout wrong would route requests to the wrong region or silently corrupt ids.
/// </summary>
[TestFixture]
public class ArgonTimeTests
{
    [Test]
    public void ToArgonTimeSeconds_IsZeroAtTheEpoch()
        => Assert.That(DateTimeOffset.Parse("2025-01-01T00:00:00+00:00").ToArgonTimeSeconds(), Is.EqualTo(0u));

    [Test]
    public void ToArgonTimeSeconds_CountsFromTheEpoch()
        => Assert.That(DateTimeOffset.Parse("2025-01-01T01:00:00+00:00").ToArgonTimeSeconds(), Is.EqualTo(3600u));

    [Test]
    public void ToArgonTimeMillis_CountsFromTheEpoch()
        => Assert.That(DateTimeOffset.Parse("2025-01-01T00:00:01+00:00").ToArgonTimeMillis(), Is.EqualTo(1000L));

    [Test]
    public void ToArgonTime_IsIndependentOfTheOffsetItIsExpressedIn()
    {
        // The same instant written in two time zones has to yield the same Argon timestamp.
        var utc   = DateTimeOffset.Parse("2026-06-01T12:00:00+00:00");
        var moscow = utc.ToOffset(TimeSpan.FromHours(3));

        Assert.That(moscow.ToArgonTimeSeconds(), Is.EqualTo(utc.ToArgonTimeSeconds()));
    }

    [Test]
    public void Pack_PutsTheTimestampInTheLeadingBytes()
    {
        const uint timestamp = 0x01020304;

        var packed = ArgonTimeExtensions.Pack(timestamp, regionId: 7, bucketCode: 0x1122, randomEntropy: 0xDEADBEEFCAFEBABE);
        var bytes  = packed.ToByteArray();

        Assert.Multiple(() =>
        {
            Assert.That(bytes[0], Is.EqualTo(0x01));
            Assert.That(bytes[1], Is.EqualTo(0x02));
            Assert.That(bytes[2], Is.EqualTo(0x03));
            Assert.That(bytes[3], Is.EqualTo(0x04));
            Assert.That(bytes[4], Is.EqualTo(7), "region id");
        });
    }

    [Test]
    public void Pack_IsDeterministicForTheSameInputs()
        => Assert.That(
            ArgonTimeExtensions.Pack(1, 2, 3, 4),
            Is.EqualTo(ArgonTimeExtensions.Pack(1, 2, 3, 4)));

    [Test]
    public void Pack_VariesWithEveryComponent()
    {
        var baseline = ArgonTimeExtensions.Pack(1, 2, 3, 4);

        Assert.Multiple(() =>
        {
            Assert.That(ArgonTimeExtensions.Pack(2, 2, 3, 4), Is.Not.EqualTo(baseline));
            Assert.That(ArgonTimeExtensions.Pack(1, 3, 3, 4), Is.Not.EqualTo(baseline));
            Assert.That(ArgonTimeExtensions.Pack(1, 2, 4, 4), Is.Not.EqualTo(baseline));
            Assert.That(ArgonTimeExtensions.Pack(1, 2, 3, 5), Is.Not.EqualTo(baseline));
        });
    }

    [Test]
    public void Pack_StoresTheReservedFlagsInTheLowNibble()
    {
        var packed = ArgonTimeExtensions.Pack(1, 2, 3, 4, reservedFlags: 0x0A);
        var bytes  = packed.ToByteArray();

        Assert.That(bytes[15] & 0x0F, Is.EqualTo(0x0A));
    }

    [Test]
    public void Pack_ChecksumCoversTheFirstFifteenBytes()
    {
        // The high nibble of the last byte is an XOR checksum; recomputing it must agree, which is
        // what lets a malformed id be rejected without a lookup.
        var packed = ArgonTimeExtensions.Pack(0x11223344, 5, 0x6677, 0x8899AABBCCDDEEFF, reservedFlags: 0x03);
        var bytes  = packed.ToByteArray();

        byte expected = 0;
        for (var i = 0; i < 15; i++)
            expected ^= bytes[i];
        expected &= 0x0F;

        Assert.That((bytes[15] >> 4) & 0x0F, Is.EqualTo(expected));
    }

    [Test]
    public void Pack_MasksReservedFlagsToFourBits()
        // Anything above the low nibble belongs to the checksum and must not bleed into it.
        => Assert.That(
            ArgonTimeExtensions.Pack(1, 2, 3, 4, reservedFlags: 0xFA),
            Is.EqualTo(ArgonTimeExtensions.Pack(1, 2, 3, 4, reservedFlags: 0x0A)));
}

/// <summary>
/// Message entities (bold, mention, attachment, …) are stored as a polymorphic JSON column. The
/// converter is what makes a stored message readable again; a round-trip failure loses formatting on
/// every historical message.
/// </summary>
[TestFixture]
public class MessageEntityConverterTests
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new MessageEntityConverter() }
    };

    private static string Serialize(IMessageEntity? entity)
        => JsonConvert.SerializeObject(entity, Settings);

    private static IMessageEntity? Deserialize(string json)
        => JsonConvert.DeserializeObject<IMessageEntity>(json, Settings);

    [Test]
    public void RoundTrips_ASimpleEntity()
    {
        var original = new MessageEntityBold(EntityType.Bold, 0, 5, 1);

        var restored = Deserialize(Serialize(original));

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.InstanceOf<MessageEntityBold>());
            Assert.That(((MessageEntityBold)restored!).offset, Is.EqualTo(0));
            Assert.That(((MessageEntityBold)restored).length, Is.EqualTo(5));
        });
    }

    [Test]
    public void RoundTrips_AnEntityCarryingPayload()
    {
        var userId   = Guid.NewGuid();
        var original = new MessageEntityMention(EntityType.Mention, 3, 8, 1, userId);

        var restored = Deserialize(Serialize(original));

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.InstanceOf<MessageEntityMention>());
            Assert.That(((MessageEntityMention)restored!).userId, Is.EqualTo(userId));
        });
    }

    [Test]
    public void PreservesTheConcreteTypeAcrossDifferentVariants()
    {
        // The discriminator is what keeps a Url from coming back as a Bold; without it the client
        // would render the wrong entity for every stored message.
        var url = new MessageEntityUrl(EntityType.Url, 1, 2, 1, "example.com", "/path");

        var restored = Deserialize(Serialize(url));

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.InstanceOf<MessageEntityUrl>());
            Assert.That(((MessageEntityUrl)restored!).domain, Is.EqualTo("example.com"));
        });
    }

    [Test]
    public void SerializesNullAsNull()
        => Assert.That(Serialize(null), Is.EqualTo("null"));

    [Test]
    public void DeserializesNullAsNull()
        => Assert.That(Deserialize("null"), Is.Null);

    [Test]
    public void WithoutADiscriminator_Throws()
        // A row written by something that bypassed this converter must fail loudly rather than
        // deserialize into a partially populated entity.
        => Assert.Throws<JsonSerializationException>(() => Deserialize("""{"offset":0,"length":5}"""));
}
