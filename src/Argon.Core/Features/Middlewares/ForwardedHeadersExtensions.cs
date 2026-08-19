namespace Argon.Features.Middlewares;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

public static class ForwardedHeadersExtensions
{
    private static readonly string[] DefaultKnownNetworks = ["10.42.0.0/16", "10.43.0.0/16"];

    /// <summary>
    /// Configures forwarded headers with trusted proxy networks from configuration.
    /// Reads CIDRs from "ForwardedHeaders:KnownNetworks" and individual IPs from "ForwardedHeaders:KnownProxies".
    /// </summary>
    public static WebApplication UseConfiguredForwardedHeaders(this WebApplication app)
        => app.UseConfiguredForwardedHeaders(
            app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? DefaultKnownNetworks,
            app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? []);

    /// <summary>
    /// The same, with the trusted hops handed in rather than read back out of configuration — for a
    /// feature that already owns the section and has had it validated.
    /// </summary>
    public static WebApplication UseConfiguredForwardedHeaders(
        this WebApplication app, IReadOnlyList<string> cidrs, IReadOnlyList<string> proxyIps)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                             | ForwardedHeaders.XForwardedHost
                             | ForwardedHeaders.XForwardedProto
        };

        foreach (var cidr in cidrs)
        {
            var parts = cidr.Split('/');
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var address)
                && int.TryParse(parts[1], out var prefixLength))
            {
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(address, prefixLength));
            }
        }

        foreach (var ip in proxyIps)
        {
            if (IPAddress.TryParse(ip, out var parsed))
                options.KnownProxies.Add(parsed);
        }

        app.UseForwardedHeaders(options);

        return app;
    }
}
