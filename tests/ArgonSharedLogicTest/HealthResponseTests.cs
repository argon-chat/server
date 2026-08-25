namespace ArgonSharedLogicTest;

using System.Net;
using System.Text.Json;
using Argon.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Who gets to see what a health check knows.
/// </summary>
/// <remarks>
/// A silo serves these on an internal port, but a client role serves them from the same Kestrel
/// listener as the public API — one listener is all a role binds. So the detailed report, which names
/// every check and dumps its whole data dictionary, is an answer about cluster internals reachable
/// from the internet unless something stops it. These tests are that something.
/// </remarks>
[TestFixture]
public class HealthResponseTests
{
    private static HealthReport Report()
        => new(new Dictionary<string, HealthReportEntry>
        {
            ["cluster"] = new(
                HealthStatus.Degraded,
                "Cluster client has no gateway",
                TimeSpan.FromMilliseconds(3),
                new InvalidOperationException("connection refused to 10.4.2.19:30000"),
                new Dictionary<string, object> { ["gateways"] = 0, ["stopping"] = true })
        }, HealthStatus.Degraded, TimeSpan.FromMilliseconds(4));

    private static HttpContext Context(string? remoteIp)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };

        context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
        context.Response.Body              = new MemoryStream();

        return context;
    }

    private async static Task<string> BodyOf(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    [Test]
    public async Task A_probe_answers_with_the_status_and_nothing_else()
    {
        var context = Context("10.4.2.7");

        await HealthCheckExtensions.WriteProbeResponse(context, Report());

        Assert.That(await BodyOf(context), Is.EqualTo("Degraded"));
    }

    /// <summary>
    /// The detail is for whoever is on the box.
    /// </summary>
    /// <remarks>
    /// Loopback rather than a private range: a pod's Service address is private too, and it is the
    /// address the whole cluster reaches it on. What this is meant to admit is a sidecar or a
    /// <c>kubectl exec</c>, both of which dial the loopback.
    /// </remarks>
    [Test]
    public async Task The_detailed_report_goes_to_a_caller_on_the_box()
    {
        var context = Context("127.0.0.1");

        await HealthCheckExtensions.WriteHealthResponse(context, Report());

        using var json = JsonDocument.Parse(await BodyOf(context));

        var check = json.RootElement.GetProperty("checks")[0];

        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("status").GetString(), Is.EqualTo("Degraded"));
            Assert.That(check.GetProperty("data").GetProperty("gateways").GetInt32(), Is.Zero);
            Assert.That(check.GetProperty("exception").GetString(), Does.Contain("connection refused"));
        });
    }

    [TestCase("203.0.113.9", TestName = "from the internet")]
    [TestCase("10.4.2.7",    TestName = "from elsewhere in the cluster")]
    public async Task Anyone_else_gets_what_a_probe_gets(string remoteIp)
    {
        var context = Context(remoteIp);

        await HealthCheckExtensions.WriteHealthResponse(context, Report());

        var body = await BodyOf(context);

        Assert.Multiple(() =>
        {
            Assert.That(body, Is.EqualTo("Degraded"));
            Assert.That(body, Does.Not.Contain("gateways"), "the count of a region's gateways");
            Assert.That(body, Does.Not.Contain("stopping"), "whether this pod is on its way out");
            Assert.That(body, Does.Not.Contain("10.4.2.19"), "an endpoint from an exception message");
        });
    }

    /// <summary>
    /// A caller with no address at all is not treated as local.
    /// </summary>
    /// <remarks>
    /// Null arrives from a Unix socket or a test host, neither of which is proof of anything. The
    /// guard fails closed for the same reason the pre-stop hook's does.
    /// </remarks>
    [Test]
    public async Task An_unknown_caller_is_not_local()
    {
        var context = Context(null);

        await HealthCheckExtensions.WriteHealthResponse(context, Report());

        Assert.That(await BodyOf(context), Is.EqualTo("Degraded"));
    }
}
