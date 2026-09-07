namespace ArgonSharedLogicTest.HealthChecks;

using Argon.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The probe endpoints must stay out of the HTTP request metrics.
/// </summary>
/// <remarks>
/// Kubernetes polls three of these per pod every few seconds and never stops, so left measured they
/// outnumber real requests by an order of magnitude: every count on a request dashboard becomes a
/// count of probes, and every latency percentile is dragged toward the cost of a health check. The
/// exclusion is one call per endpoint and silent when it is missing — nothing fails, the charts just
/// quietly go back to being about Kubernetes — which is why it is pinned here.
/// </remarks>
[TestFixture]
public class ProbeEndpointMetricsTests
{
    private static readonly string[] ProbePaths =
        ["/health/startup", "/health/live", "/health/ready", "/health"];

    private static IReadOnlyList<RouteEndpoint> MappedProbes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddHealthChecks();

        var app = builder.Build();
        app.MapProbeEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources
           .SelectMany(source => source.Endpoints)
           .OfType<RouteEndpoint>()
           .ToArray();
    }

    [Test]
    public void EveryProbeEndpointIsMapped()
    {
        var mapped = MappedProbes().Select(endpoint => $"/{endpoint.RoutePattern.RawText?.TrimStart('/')}");

        Assert.That(mapped, Is.EquivalentTo(ProbePaths));
    }

    [Test]
    public void NoProbeEndpointIsCountedInHttpRequestMetrics()
    {
        var measured = MappedProbes()
           .Where(endpoint => endpoint.Metadata.GetMetadata<IDisableHttpMetricsMetadata>() is null)
           .Select(endpoint => endpoint.RoutePattern.RawText)
           .ToArray();

        Assert.That(measured, Is.Empty,
            "probe endpoints missing DisableHttpMetrics(): " + string.Join(", ", measured));
    }
}
