namespace ArgonSharedLogicTest;

using System.Net;
using Argon.Features;
using Argon.Features.Middlewares;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Who a request is allowed to claim to be from.
/// </summary>
/// <remarks>
/// The value under test is the one the anonymous rate limits are counted against, the one the captcha
/// is verified with, and the one a user later reads back as "where was I signed in from". Every source
/// it can come from except the socket is a header, so the whole question is which hop delivered it.
/// </remarks>
public class ForwardedHeadersTrustTests
{
    private static readonly IList<IPNetwork> PodNetwork  = [new(IPAddress.Parse("10.42.0.0"), 16)];
    private static readonly IList<IPAddress> NoProxies   = [];

    private static DefaultHttpContext RequestFrom(string peer, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();

        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);

        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;

        return context;
    }

    /// <summary>
    /// The case the whole mechanism exists for: a caller that talks to the process directly and
    /// announces itself as somebody else.
    /// </summary>
    /// <remarks>
    /// Both headers are set, and the one checked first is the one Cloudflare would have written — so
    /// this fails if either the ladder is consulted at all or only its lower rungs are guarded.
    /// </remarks>
    [Test]
    public void A_caller_that_is_not_a_known_proxy_cannot_name_its_own_address()
    {
        var context = RequestFrom("203.0.113.7",
            ("CF-Connecting-IP", "1.2.3.4"),
            ("X-Forwarded-For", "1.2.3.4"));

        ProxyTrust.Evaluate(context, PodNetwork, NoProxies);

        Assert.That(context.GetIpAddress(), Is.EqualTo("203.0.113.7"));
    }

    /// <summary>
    /// A role that never enabled the feature has no proxy in front of it as far as it knows, so nothing
    /// evaluates anything and the headers must still not be believed.
    /// </summary>
    /// <remarks>
    /// This is the default that has to hold, not a configured behaviour: silence means untrusted. The
    /// alternative — absent marking reading as "fine" — would leave every role that forgot the feature
    /// exactly as spoofable as before, which is the bug this change is about.
    /// </remarks>
    [Test]
    public void An_unevaluated_request_is_not_trusted()
    {
        var context = RequestFrom("10.42.0.9", ("X-Forwarded-For", "1.2.3.4"));

        Assert.That(context.ArrivedThroughTrustedProxy(), Is.False);
        Assert.That(context.GetIpAddress(), Is.EqualTo("10.42.0.9"));
    }

    [Test]
    public void A_proxy_inside_a_known_network_speaks_for_its_caller()
    {
        var context = RequestFrom("10.42.0.9", ("X-Forwarded-For", "1.2.3.4"));

        Assert.That(ProxyTrust.Evaluate(context, PodNetwork, NoProxies), Is.True);
        Assert.That(context.GetIpAddress(), Is.EqualTo("1.2.3.4"));
    }

    /// <summary>
    /// The client is the leftmost entry; the rest of the list is the proxies it passed through.
    /// </summary>
    [Test]
    public void The_first_entry_of_a_chain_is_the_caller()
    {
        var context = RequestFrom("10.42.0.9", ("X-Forwarded-For", "1.2.3.4, 10.42.0.3, 10.42.0.9"));

        ProxyTrust.Evaluate(context, PodNetwork, NoProxies);

        Assert.That(context.GetIpAddress(), Is.EqualTo("1.2.3.4"));
    }

    /// <summary>
    /// A v4 proxy reaching a dual-stack listener arrives as <c>::ffff:172.20.0.2</c>, and has to match
    /// the <c>172.20.0.2</c> the operator wrote in KnownProxies.
    /// </summary>
    /// <remarks>
    /// <c>IPAddress.Equals</c> treats the two forms as different addresses, so this is the comparison
    /// that has to unmap first — the CIDR list does not, because <c>IPNetwork.Contains</c> unmaps on its
    /// own. Getting it wrong reports nothing: a correctly configured deployment keeps serving, with
    /// every caller silently collapsed into the proxy's identity — one rate-limit bucket for everyone.
    /// </remarks>
    [Test]
    public void A_v4_proxy_mapped_into_v6_still_matches_the_address_it_was_named_by()
    {
        var context = RequestFrom("::ffff:172.20.0.2", ("X-Forwarded-For", "1.2.3.4"));

        var trusted = ProxyTrust.Evaluate(context, [], [IPAddress.Parse("172.20.0.2")]);

        Assert.That(trusted, Is.True);
        Assert.That(context.GetIpAddress(), Is.EqualTo("1.2.3.4"));
    }

    /// <summary>
    /// KnownProxies is the escape hatch for an edge that is not inside any configured range — a Traefik
    /// container on a compose bridge the operator did not enumerate, say.
    /// </summary>
    [Test]
    public void A_named_proxy_outside_every_network_is_still_trusted()
    {
        var context = RequestFrom("172.20.0.2", ("X-Forwarded-For", "1.2.3.4"));

        var trusted = ProxyTrust.Evaluate(context, PodNetwork, [IPAddress.Parse("172.20.0.2")]);

        Assert.That(trusted, Is.True);
        Assert.That(context.GetIpAddress(), Is.EqualTo("1.2.3.4"));
    }

    [Test]
    public void An_empty_trust_list_trusts_nobody()
    {
        var context = RequestFrom("10.42.0.9", ("X-Forwarded-For", "1.2.3.4"));

        Assert.That(ProxyTrust.Evaluate(context, [], NoProxies), Is.False);
        Assert.That(context.GetIpAddress(), Is.EqualTo("10.42.0.9"));
    }

    /// <summary>
    /// Country rides on the same headers and is gated by the same rule.
    /// </summary>
    /// <remarks>
    /// Lower stakes than the address, but the same shape: a caller that can choose its own country can
    /// choose which regional rules it is judged by. Unknown is what a request without an edge in front
    /// of it has always reported.
    /// </remarks>
    [Test]
    public void Country_from_an_untrusted_hop_is_unknown()
    {
        var context = RequestFrom("203.0.113.7", ("CF-IPCountry", "US"));

        ProxyTrust.Evaluate(context, PodNetwork, NoProxies);

        Assert.That(context.GetRegion(), Is.EqualTo("00"));

        var behindEdge = RequestFrom("10.42.0.9", ("CF-IPCountry", "US"));

        ProxyTrust.Evaluate(behindEdge, PodNetwork, NoProxies);

        Assert.That(behindEdge.GetRegion(), Is.EqualTo("US"));
    }
}
