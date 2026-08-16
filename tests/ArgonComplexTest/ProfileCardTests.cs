namespace ArgonComplexTest.Tests;

using ArgonContracts;

/// <summary>
/// The two profile-card fields that had a reader but no writer: the "about me" text, and the
/// "in Argon since …" date.
/// </summary>
/// <remarks>
/// Bio was readable through <c>ArgonUserProfile.bio</c> long before anything could set it, so the
/// round trip is the thing to pin — a write that lands in the wrong column reads back as null and
/// looks identical to "not set yet". The registration date is the other half of the card's
/// membership line (<c>Friendship.friendAt</c> is the first), and it is derived rather than stored
/// on the profile row, which is exactly the kind of derivation that silently returns
/// <c>0001-01-01</c> when the join it depends on is not loaded.
/// </remarks>
[TestFixture]
public class ProfileCardTests : TestBase
{
    private static UserEditInput BioOnly(string? bio)
        => new(null, null, null, null, null, null, null, null, null, null, bio);

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateMe_Bio_RoundTripsThroughGetMyProfile(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        var result = await user.Users.UpdateMe(BioOnly("Строю ракеты по выходным."), ct);

        Assert.That(result, Is.InstanceOf<SuccessUpdateMe>(),
            $"Bio was refused: {(result as FailedUpdateMe)?.error}");

        // Both the echo and a fresh read: the echo proves the write path built the DTO, the fetch
        // proves it reached the database rather than only the response.
        Assert.That(((SuccessUpdateMe)result).profile.bio, Is.EqualTo("Строю ракеты по выходным."));

        var profile = await user.Users.GetMyProfile(ct);
        Assert.That(profile.bio, Is.EqualTo("Строю ракеты по выходным."));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateMe_Bio_IsNotAPremiumField(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        // A freshly registered account has no Ultima. Bio sits next to the premium cosmetics in
        // UserEditInput, so it is one careless `||` away from being gated behind a subscription.
        var result = await user.Users.UpdateMe(BioOnly("free as in beer"), ct);

        Assert.That(result, Is.InstanceOf<SuccessUpdateMe>(),
            $"Bio behaved like a premium field: {(result as FailedUpdateMe)?.error}");
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateMe_AnEmptyBio_ClearsIt(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        await user.Users.UpdateMe(BioOnly("something to say"), ct);
        await user.Users.UpdateMe(BioOnly(""), ct);

        // An emptied bio has to read back as absent, not as present-but-blank, or the card renders
        // an empty paragraph where it should render nothing at all.
        var profile = await user.Users.GetMyProfile(ct);
        Assert.That(profile.bio, Is.Null);
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateMe_ABioOverTheColumnLimit_IsRefusedRatherThanTruncated(CancellationToken ct = default)
    {
        var user = await CreateSessionAsync(ct);

        var result = await user.Users.UpdateMe(BioOnly(new string('я', 513)), ct);

        Assert.That(result, Is.InstanceOf<FailedUpdateMe>());
        Assert.That(((FailedUpdateMe)result).error, Is.EqualTo(UpdateMeError.BIO_TOO_LONG));

        // Cutting somebody's "about me" mid-sentence without telling them is worse than refusing it.
        var profile = await user.Users.GetMyProfile(ct);
        Assert.That(profile.bio, Is.Null);
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task GetMyProfile_ReportsWhenTheAccountWasRegistered(CancellationToken ct = default)
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var user   = await CreateSessionAsync(ct);

        var profile = await user.Users.GetMyProfile(ct);

        Assert.That(profile.registeredAt, Is.Not.Null, "the card has no date to put after «В Argon с»");

        // The failure this guards is a default(DateTime) sneaking through — it deserialises fine and
        // renders as «В Argon с 1 января 0001».
        Assert.That(profile.registeredAt!.Value, Is.GreaterThan(before));
        Assert.That(profile.registeredAt!.Value, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task PrefetchProfile_ReportsTheRegistrationDateOfAnotherMember(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var created = await owner.Users.CreateSpace(new CreateServerRequest("Card Space", "Description", string.Empty), ct);
        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>());
        var spaceId = ((SuccessCreateSpace)created).space.spaceId;

        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);
        Assert.That(joined, Is.InstanceOf<SuccessJoin>());

        // The card someone else opens goes through PrefetchProfile, which loads the profile via the
        // member → user → profile chain rather than on its own — a different path to the same field.
        var profile = await owner.Servers.PrefetchProfile(spaceId, guest.UserId, ct);

        Assert.That(profile.userId, Is.EqualTo(guest.UserId));
        Assert.That(profile.registeredAt, Is.Not.Null);
        Assert.That(profile.registeredAt!.Value, Is.GreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5)));
    }
}
