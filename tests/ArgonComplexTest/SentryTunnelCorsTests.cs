namespace ArgonComplexTest;

using System.Net.Http.Headers;
using System.Text;

/// <summary>
/// The error-reporting tunnel, asked for from an origin the API does not otherwise trust.
/// </summary>
/// <remarks>
/// <para>The tunnel exists so a browser can report a crash without talking to Sentry directly, and
/// the browsers that need it are on origins the API has no reason to allowlist: a self-hosted web
/// client, or a developer on <c>https://localhost:5005</c> pointed at the real API. So its response
/// has to carry <c>Access-Control-Allow-Origin</c> whatever the origin is.</para>
///
/// <para><b>The failure this guards is invisible from the server side.</b> The tunnel middleware
/// maps before routing, answers the request and stops — so it ran before the CORS middleware ever
/// did, and the response left with <c>200 OK</c> and no header on it. Every server-side signal said
/// success: the status, the log line, the Sentry envelope forwarded. Only the browser knew, and what
/// it reports is <c>net::ERR_FAILED</c>, which reads as the network being down rather than as a
/// header being absent.</para>
/// </remarks>
[TestFixture]
public class SentryTunnelCorsTests : TestBase
{
    /// <summary>
    /// An origin the default allowlist genuinely refuses.
    /// </summary>
    /// <remarks>
    /// NOT <c>https://localhost:5005</c>, which is what the report came from and the obvious choice.
    /// The default allowlist already contains <c>https://localhost</c> at any port, so a test built
    /// on it would pass the moment the tunnel merely reached the CORS middleware, and would say
    /// nothing about whether the tunnel accepts an origin the API refuses — which is the whole
    /// property. That the reporter's own origin happened to be allowlisted is also why the bug looked
    /// so odd from outside: the API would have welcomed it, and the tunnel answered before anything
    /// asked.
    /// </remarks>
    private const string ForeignOrigin = "https://not-on-the-allowlist.example";

    private const string TunnelPath = "/k";

    private static HttpRequestMessage Tunnel(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, TunnelPath);
        request.Headers.Add("Origin", ForeignOrigin);
        return request;
    }

    private static string? AllowedOrigin(HttpResponseMessage response)
        => response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
            ? values.FirstOrDefault()
            : null;

    /// <summary>
    /// A report from an untrusted origin comes back with the header that lets the page see the answer.
    /// </summary>
    /// <remarks>
    /// The status is deliberately not asserted. What the tunnel does with an envelope — forwards it,
    /// rejects it, finds no Sentry to send it to on a test host — is a different question, and the
    /// header has to be there either way: a rejected report the page can read is a bug report, and a
    /// rejected report it cannot read is a mystery.
    /// </remarks>
    [Test]
    public async Task A_report_from_a_foreign_origin_comes_back_readable()
    {
        var request = Tunnel(HttpMethod.Post);

        // Shaped like a Sentry envelope rather than empty, so this exercises the tunnel rather than
        // its argument validation.
        request.Content = new StringContent(
            """{"event_id":"00000000000000000000000000000000","dsn":"https://key@example.invalid/1"}""" + "\n"
          + """{"type":"event"}""" + "\n"
          + """{"message":"from a test"}""" + "\n",
            Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };

        var response = await HttpClient.SendAsync(request);

        Assert.That(AllowedOrigin(response), Is.Not.Null,
            "the tunnel answered without Access-Control-Allow-Origin, so the browser discards the "
          + "response whatever the status line said");
    }

    /// <summary>
    /// The preflight is answered by this policy, and not by the library underneath it.
    /// </summary>
    /// <remarks>
    /// <para><b>The preflight was never the broken half</b>, which is worth writing down because the
    /// obvious version of this test passes without the fix. Sentry's own tunnel middleware answers
    /// <c>OPTIONS</c> on this path already: measured on a build with the branch removed, <c>/k</c>
    /// returned <c>200</c> carrying the full CORS set, while the same preflight sent at
    /// <c>/not-the-tunnel</c> and at an Ion route came back <c>204</c> with no CORS header at all. So
    /// merely asserting that the header is present measures the library, not this pipeline.</para>
    ///
    /// <para>What separates the two is <c>Access-Control-Allow-Credentials</c>. The tunnel middleware
    /// emits it; the policy here cannot, because <c>AllowAnyOrigin</c> and <c>AllowCredentials</c> are
    /// mutually exclusive in ASP.NET. Its absence is therefore the evidence that the branch in front
    /// ran and short-circuited before the tunnel was reached — which is the ordering the POST depends
    /// on, and the only part of this a browser would notice if it regressed.</para>
    /// </remarks>
    [Test]
    public async Task A_preflight_on_the_tunnel_is_answered_by_the_open_policy()
    {
        var request = Tunnel(HttpMethod.Options);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await HttpClient.SendAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(AllowedOrigin(response), Is.Not.Null, "a preflight without the header fails the check");
            Assert.That(response.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods)
                     && methods.Any(m => m.Contains("POST", StringComparison.OrdinalIgnoreCase)),
                Is.True, "and it has to admit the method the reporter is about to use");
            Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials"), Is.False,
                "the credentialed answer is Sentry's tunnel middleware replying, which means the CORS "
              + "branch did not run ahead of it — the preflight would still pass, and the POST that "
              + "follows it would not");
        });
    }

    /// <summary>
    /// And the permissive policy stops at the tunnel.
    /// </summary>
    /// <remarks>
    /// <para>This is the half that makes the other two worth having. Putting a second CORS middleware
    /// in front of everything would have fixed <c>/k</c> just as well and opened the whole API to any
    /// origin on the way — a change that passes every test aimed at the tunnel and is visible only
    /// from somewhere else.</para>
    ///
    /// <para>So the same untrusted origin is sent at an ordinary endpoint, and must not be told it is
    /// welcome. The status does not matter here either; the header does.</para>
    /// </remarks>
    [Test]
    public async Task The_open_policy_does_not_reach_the_rest_of_the_api()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/ion/IUserInteraction/GetMe.unary");
        request.Headers.Add("Origin", ForeignOrigin);
        request.Content = new StringContent("", Encoding.UTF8);

        var response = await HttpClient.SendAsync(request);

        Assert.That(AllowedOrigin(response), Is.Null,
            $"'{ForeignOrigin}' is not on the API's allowlist, so an endpoint that is not the tunnel "
          + "must not hand it a permissive CORS header — if this fails, the tunnel's policy is being "
          + "applied to the whole pipeline");
    }
}
