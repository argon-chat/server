namespace ArgonSharedLogicTest.Aegis;

using Argon.Features.Aegis;

/// <summary>
/// The one place an avatar's public address is composed.
/// </summary>
/// <remarks>
/// <para>It became one place after being three. <c>userinfo</c> built it from the configured base;
/// the account picker's Vue template built <c>https://ru.cdn.argon.gl/{id}</c> with the region typed
/// in; the developer console built <c>https://eu.argon.zone/{id}</c> with a different one. Two of
/// those name a host that only exists in Argon's own deployment, and each is right for exactly one
/// region — so a user outside it, or anybody self-hosting, got a broken image.</para>
///
/// <para>What makes an id the wrong thing to hand a browser is that it says where a file sits in
/// some deployment's storage and nothing about how that deployment publishes it. Only the server
/// knows the second, so the server is what says it, and this is the rule it says it by.</para>
/// </remarks>
[TestFixture]
public class AvatarAddressTests
{
    private const string FileId = "0195f3c0-1f4c-7c4f-9a3e-6b1d2c8e4a71";

    private static AegisOptions With(string baseUrl)
        => new() { AvatarBaseUrl = baseUrl };

    /// <summary>
    /// The address is the API's file redirect, not a storage host.
    /// </summary>
    /// <remarks>
    /// The redirect picks a regional mirror per request, which is what lets one address stay correct
    /// while mirrors are added, moved or retired. A URL naming a mirror is correct only until it is
    /// not, and by then a third party has it stored.
    /// </remarks>
    [Test]
    public void An_avatar_is_addressed_through_the_api_that_redirects_to_a_mirror()
        => Assert.That(With("https://api.argon.gl").AvatarUrlFor(FileId),
            Is.EqualTo($"https://api.argon.gl/files/{FileId}"));

    [Test]
    public void A_trailing_slash_on_the_base_does_not_double_up()
        => Assert.That(With("https://api.argon.gl/").AvatarUrlFor(FileId),
            Is.EqualTo($"https://api.argon.gl/files/{FileId}"));

    /// <summary>
    /// No configured base means no address — never a relative one.
    /// </summary>
    /// <remarks>
    /// These URLs are rendered by pages served from other origins: the widget on the identity
    /// server's host, the console on its own, a third-party application anywhere at all. A relative
    /// path resolves against whichever of those received it, so it would name the one host it
    /// certainly does not mean. Null instead, which every call site already renders as initials.
    /// </remarks>
    [Test]
    public void An_unconfigured_base_yields_nothing_rather_than_a_relative_path()
        => Assert.That(With("").AvatarUrlFor(FileId), Is.Null);

    [Test]
    public void An_account_with_no_avatar_yields_nothing()
    {
        Assert.That(With("https://api.argon.gl").AvatarUrlFor(null), Is.Null);
        Assert.That(With("https://api.argon.gl").AvatarUrlFor(""), Is.Null);
    }
}
