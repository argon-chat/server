namespace Argon.Features.Middlewares;

using Argon.Features.Clustering;

/// <summary>
/// Which proxies this process believes about who the caller is.
/// </summary>
/// <remarks>
/// <c>X-Forwarded-For</c> and friends are headers, so anyone can write them; what makes them
/// trustworthy is arriving from a hop that is known to overwrite rather than append. That is what
/// this list is. Widening it to an address an untrusted client can reach hands every caller the
/// ability to claim any source address, which the anonymous rate limits are counted against.
/// </remarks>
public sealed class ArgonForwardedHeadersOptions : IValidatableFeatureOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Trusted proxy networks in CIDR form. The default is the in-cluster pod and service ranges.</summary>
    public List<string> KnownNetworks { get; set; } = ["10.42.0.0/16", "10.43.0.0/16"];

    /// <summary>Individual trusted proxy addresses, for a proxy outside any of the networks above.</summary>
    public List<string> KnownProxies { get; set; } = [];

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        foreach (var cidr in KnownNetworks)
            report.Require(System.Net.IPNetwork.TryParse(cidr, out _), nameof(KnownNetworks),
                $"'{cidr}' is not a CIDR range");

        foreach (var proxy in KnownProxies)
            report.Require(System.Net.IPAddress.TryParse(proxy, out _), nameof(KnownProxies),
                $"'{proxy}' is not an IP address");

        report.Require(KnownNetworks.Count > 0 || KnownProxies.Count > 0, nameof(KnownNetworks),
            "and knownProxies are both empty, so forwarded headers would be ignored and every " +
            "request would appear to come from the proxy");
    }
}
