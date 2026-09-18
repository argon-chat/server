namespace ArgonSharedLogicTest;

using Argon.Features.WebSession;
using Microsoft.AspNetCore.Http;

/// <summary>
/// That a device binding can read the session cookie, and an API call still cannot do so blindly.
/// </summary>
/// <remarks>
/// <para><b>This pins a difference that looks like an inconsistency.</b> Two readers for one cookie
/// invites tidying them into one, and either direction of tidying breaks something: make the
/// binding strict and every registration 401s, because DBSC requests are issued by the browser's
/// own network stack and carry no fetch metadata; make the API lenient and the cookie becomes
/// reachable from any site that can cause a credentialed request.</para>
///
/// <para>It is worth a test rather than a comment because the failure it prevents is invisible —
/// registration fails, the browser retries on its own schedule, and the only trace is a 401 in an
/// internals page nobody has open.</para>
/// </remarks>
[TestFixture]
public class DeviceBindingCookieTests
{
    private static HttpContext Request(string? fetchSite)
    {
        var options = new WebSessionOptions();
        var http    = new DefaultHttpContext();

        http.Request.Headers["Cookie"] = $"{options.CookieName}=refresh-token";

        if (fetchSite is not null)
            http.Request.Headers["Sec-Fetch-Site"] = fetchSite;

        return http;
    }

    /// <summary>The case that was failing in production: no fetch metadata, because no page asked.</summary>
    [Test]
    public void ADeviceBindingReadsTheCookieWithoutFetchMetadata()
        => Assert.That(WebSessionCookie.ReadForDeviceBinding(Request(null), new WebSessionOptions()),
            Is.EqualTo("refresh-token"));

    [Test]
    public void AnOrdinaryReadRefusesTheSameRequest()
        => Assert.That(WebSessionCookie.Read(Request(null), new WebSessionOptions()), Is.Null);

    [Test]
    public void AnOrdinaryReadStillWorksForAPage()
        => Assert.That(WebSessionCookie.Read(Request("same-site"), new WebSessionOptions()),
            Is.EqualTo("refresh-token"));

    /// <remarks>
    /// The binding reader is deliberately indifferent to where the request came from — its safety
    /// comes from the challenge, not from this — so a cross-site label changes nothing for it.
    /// </remarks>
    [Test]
    public void ADeviceBindingDoesNotCareWhatTheLabelSays()
        => Assert.That(WebSessionCookie.ReadForDeviceBinding(Request("cross-site"), new WebSessionOptions()),
            Is.EqualTo("refresh-token"));

    [Test]
    public void NeitherInventsACookieThatIsNotThere()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        Assert.That(WebSessionCookie.Read(http, new WebSessionOptions()), Is.Null);
        Assert.That(WebSessionCookie.ReadForDeviceBinding(http, new WebSessionOptions()), Is.Null);
    }
}
