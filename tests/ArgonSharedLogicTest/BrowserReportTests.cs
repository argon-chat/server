namespace ArgonSharedLogicTest;

using System.Text;
using Argon.Features.Reporting;
using Argon.Features.WebSession;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The gates on the browser report endpoint.
/// </summary>
/// <remarks>
/// <para>This endpoint has to be unauthenticated — a browser reporting that its tab was killed has
/// no session left to present — so everything protecting it is a filter, and a filter that is
/// quietly relaxed still passes every test about the happy path. These are the tests about the
/// unhappy one.</para>
///
/// <para>Two of them guard against different kinds of pollution. The origin check is what stops a
/// stranger writing into somebody's Sentry quota; the type filter is what stops CSP violations
/// being filed twice, once by Sentry's own endpoint and once by this one, which would look like a
/// doubling of the problem rather than a doubling of the reporting.</para>
/// </remarks>
[TestFixture]
public class BrowserReportTests
{
    private const string OurOrigin = "https://app.argon.gl";

    private static WebSessionOptions Settings() => new()
    {
        TrustedApplications = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A37E7A1DB06E9610C9C0BD77C61A821B"] = [OurOrigin],
        },
    };

    private static HttpContext Request(string? origin = OurOrigin, string? contentType = "application/reports+json")
    {
        var http = new DefaultHttpContext();

        if (origin is not null) http.Request.Headers["Origin"] = origin;
        if (contentType is not null) http.Request.ContentType = contentType;

        return http;
    }

    // ── who may report ────────────────────────────────────────────────────────────────────────

    [Test]
    public void AcceptsAReportFromAClientThisDeploymentServes()
        => Assert.That(BrowserReportEndpoints.IsOurOrigin(Request(), Settings()), Is.True);

    [Test]
    public void RefusesAReportFromAnywhereElse()
        => Assert.That(BrowserReportEndpoints.IsOurOrigin(Request("https://evil.example"), Settings()), Is.False);

    /// <remarks>A browser always sends one on these; something with no Origin is not a browser.</remarks>
    [Test]
    public void RefusesAReportThatNamesNoOrigin()
        => Assert.That(BrowserReportEndpoints.IsOurOrigin(Request(origin: null), Settings()), Is.False);

    /// <summary>An empty allowlist trusts nobody, rather than everybody.</summary>
    [Test]
    public void RefusesEverythingWhenNoClientIsConfigured()
        => Assert.That(BrowserReportEndpoints.IsOurOrigin(Request(), new WebSessionOptions()), Is.False);

    // ── what may be reported ──────────────────────────────────────────────────────────────────

    [Test]
    public void TakesTheTypesThatReachUsNoOtherWay()
    {
        Assert.That(BrowserReportEndpoints.Accepted, Does.Contain("crash"));
        Assert.That(BrowserReportEndpoints.Accepted, Does.Contain("deprecation"));
        Assert.That(BrowserReportEndpoints.Accepted, Does.Contain("intervention"));
    }

    /// <summary>
    /// CSP goes to Sentry's own endpoint. Taking it here as well would file each violation twice.
    /// </summary>
    [Test]
    public void LeavesCspViolationsToSentry()
        => Assert.That(BrowserReportEndpoints.Accepted, Does.Not.Contain("csp-violation"));

    [Test]
    public void IgnoresATypeSomebodyInvented()
        => Assert.That(BrowserReportEndpoints.Accepted.Contains("definitely-not-a-report-type"), Is.False);

    // ── what a report must look like ──────────────────────────────────────────────────────────

    [Test]
    public void RequiresTheReportingApiContentType()
        => Assert.That(BrowserReportEndpoints.IsReportBody(Request()), Is.True);

    /// <remarks>Browsers append one; matching on the prefix is why this passes.</remarks>
    [Test]
    public void AllowsACharsetOnTheContentType()
        => Assert.That(
            BrowserReportEndpoints.IsReportBody(Request(contentType: "application/reports+json; charset=utf-8")),
            Is.True);

    [Test]
    public void RefusesAnOrdinaryJsonPost()
        => Assert.That(BrowserReportEndpoints.IsReportBody(Request(contentType: "application/json")), Is.False);

    [Test]
    public void RefusesAPostWithNoContentTypeAtAll()
        => Assert.That(BrowserReportEndpoints.IsReportBody(Request(contentType: null)), Is.False);

    // ── limits ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void TruncatesAFieldLongerThanAnythingWorthKeeping()
    {
        var truncated = BrowserReportEndpoints.Truncate(new string('x', 5000));

        Assert.That(truncated, Has.Length.EqualTo(512));
    }

    [Test]
    public void LeavesAnOrdinaryFieldAlone()
        => Assert.That(BrowserReportEndpoints.Truncate("https://app.argon.gl/space/1"),
            Is.EqualTo("https://app.argon.gl/space/1"));

    [Test]
    public void HandlesAnAbsentField()
        => Assert.That(BrowserReportEndpoints.Truncate(null), Is.Null);

    /// <summary>
    /// The read stops at the buffer, so a body that never ends cannot be turned into memory.
    /// </summary>
    [Test]
    public async Task ReadsNoMoreThanTheBufferHolds()
    {
        var body   = new MemoryStream(Encoding.UTF8.GetBytes(new string('y', 4096)));
        var buffer = new byte[256];

        var read = await BrowserReportEndpoints.ReadCappedAsync(body, buffer, CancellationToken.None);

        Assert.That(read, Is.EqualTo(256));
        Assert.That(body.Position, Is.LessThanOrEqualTo(4096));
    }

    [Test]
    public async Task ReadsAShortBodyWhole()
    {
        var payload = Encoding.UTF8.GetBytes("[{\"type\":\"crash\"}]");
        var read    = await BrowserReportEndpoints.ReadCappedAsync(
            new MemoryStream(payload), new byte[1024], CancellationToken.None);

        Assert.That(read, Is.EqualTo(payload.Length));
    }
}
