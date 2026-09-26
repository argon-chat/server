namespace ArgonSharedLogicTest;

using System.Net;
using Argon.Core.Features.WebHooks;
using Argon.Entities;

/// <summary>
/// What the anonymous webhook endpoint decides before the cluster is asked, and the rules a webhook's
/// name and a post's <c>username</c> are held to.
/// </summary>
[TestFixture]
public class ChannelWebhookRulesTests
{
    // Written as code points so no invisible character sits in this file.
    private const char Bel  = (char)0x0007;
    private const char Zwsp = (char)0x200B;
    private const char Zwnj = (char)0x200C;
    private const char Zwj  = (char)0x200D;
    private const char Lrm  = (char)0x200E;
    private const char Lre  = (char)0x202A;
    private const char Rlo  = (char)0x202E;
    private const char Lri  = (char)0x2066;
    private const char Pdi  = (char)0x2069;
    private const char Bom  = (char)0xFEFF;
    private const char Ls   = (char)0x2028;

    private static string S(params object[] parts) => string.Concat(parts);

    private static string Fullwidth(string ascii) => new(ascii.Select(c => (char)(c + 0xFEE0)).ToArray());

    [Test]
    public void An_issued_token_has_the_shape_the_endpoint_accepts()
    {
        for (var i = 0; i < 200; i++)
            Assert.That(ChannelWebhookEntity.IsTokenShaped(ChannelWebhookEntity.NewToken()), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-the-token")]
    [TestCase("Qx3v-8kP_2mN7rT1yU4iO9pA6sD5fG0hJ3kL2zX1cV")]
    [TestCase("Qx3v-8kP_2mN7rT1yU4iO9pA6sD5fG0hJ3kL2zX1cV8A")]
    [TestCase("Qx3v+8kP/2mN7rT1yU4iO9pA6sD5fG0hJ3kL2zX1cV8")]
    [TestCase("Qx3v-8kP_2mN7rT1yU4iO9pA6sD5fG0hJ3kL2zX1c==")]
    [TestCase("Qx3v-8kP_2mN7rT1yU4iO9pA6sD5fG0hJ3kL2zX1cV%")]
    public void Anything_else_is_not_a_token(string? token)
        => Assert.That(ChannelWebhookEntity.IsTokenShaped(token), Is.False);

    [Test]
    public void A_non_ascii_letter_is_not_base64url()
        => Assert.That(ChannelWebhookEntity.IsTokenShaped(S("Qx3v-8kP_2mN7rT1yU4iO9pA6sD5fG0hJ3kL2zX1cV", (char)0xE9)), Is.False);

    [Test]
    public void Control_bidi_and_zero_width_characters_are_stripped()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChannelWebhookEntity.CleanName("  Release bot  "), Is.EqualTo("Release bot"));
            Assert.That(ChannelWebhookEntity.CleanName(S(Rlo, "Release", Zwsp, " bot", Bel)), Is.EqualTo("Release bot"));
            Assert.That(ChannelWebhookEntity.CleanName(S("De", Lri, "ploy", Pdi, " ", Bom, "bot", Zwj)), Is.EqualTo("Deploy bot"));
            Assert.That(ChannelWebhookEntity.CleanName(S("line", Ls, "break")), Is.EqualTo("linebreak"));
            Assert.That(ChannelWebhookEntity.CleanName(S(Zwsp, Zwnj, Lrm, Lre)), Is.Empty);

            var kept = S("Caf", (char)0xE9, " ", char.ConvertFromUtf32(0x1F680));
            Assert.That(ChannelWebhookEntity.CleanName(kept), Is.EqualTo(kept), "letters and emoji are not invisible");
        });
    }

    [TestCase("argon")]
    [TestCase("SYSTEM")]
    [TestCase("Moderator")]
    [TestCase("admin")]
    [TestCase("Administrator")]
    [TestCase("official")]
    [TestCase("Support")]
    [TestCase("staff")]
    public void Names_that_pass_for_the_platform_are_reserved(string name)
        => Assert.That(ChannelWebhookEntity.IsReservedName(name), Is.True);

    [Test]
    public void A_fullwidth_spelling_is_the_same_name()
        => Assert.That(ChannelWebhookEntity.IsReservedName(Fullwidth("ARGON")), Is.True);

    [TestCase("Argon fan club")]
    [TestCase("Release bot")]
    [TestCase("admins")]
    public void Other_names_are_not(string name)
        => Assert.That(ChannelWebhookEntity.IsReservedName(name), Is.False);

    [Test]
    public void The_rate_limit_partitions_by_address_and_by_ipv6_64()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IncomingWebhookEndpoint.AddressKey(null), Is.Null);
            Assert.That(IncomingWebhookEndpoint.AddressKey(IPAddress.Parse("203.0.113.7")), Is.EqualTo("203.0.113.7"));
            Assert.That(IncomingWebhookEndpoint.AddressKey(IPAddress.Parse("::ffff:203.0.113.7")), Is.EqualTo("203.0.113.7"));
            Assert.That(IncomingWebhookEndpoint.AddressKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")),
                Is.EqualTo(IncomingWebhookEndpoint.AddressKey(IPAddress.Parse("2001:db8:1:2::1"))));
            Assert.That(IncomingWebhookEndpoint.AddressKey(IPAddress.Parse("2001:db8:1:2::1")),
                Is.Not.EqualTo(IncomingWebhookEndpoint.AddressKey(IPAddress.Parse("2001:db8:1:3::1"))));
        });
    }
}
