namespace Argon.Features.Middlewares;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

public static class ForwardedHeadersExtensions
{
    private static readonly string[] DefaultKnownNetworks = ["10.42.0.0/16", "10.43.0.0/16"];

    /// <summary>
    /// Configures forwarded headers with trusted proxy networks from configuration.
    /// Reads CIDRs from "ForwardedHeaders:KnownNetworks" section, defaults to K3s pod/service CIDRs.
    /// Also strips X-Forwarded-Tls-Client-Cert from untrusted sources.
    /// </summary>
    public static WebApplication UseConfiguredForwardedHeaders(this WebApplication app)
    {
        var cidrs = app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>()
                    ?? DefaultKnownNetworks;

        var trustedNetworks = new List<System.Net.IPNetwork>();
        foreach (var cidr in cidrs)
        {
            var parts = cidr.Split('/');
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var address)
                && int.TryParse(parts[1], out var prefixLength))
            {
                trustedNetworks.Add(new System.Net.IPNetwork(address, prefixLength));
            }
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                             | ForwardedHeaders.XForwardedHost
                             | ForwardedHeaders.XForwardedProto
        };

        foreach (var network in trustedNetworks)
            options.KnownIPNetworks.Add(network);

        app.UseForwardedHeaders(options);

        // Strip X-Forwarded-Tls-Client-Cert from requests not originating from trusted proxy networks
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Forwarded-Tls-Client-Cert"))
            {
                var remoteIp = context.Connection.RemoteIpAddress;
                if (remoteIp is null || !IsInTrustedNetwork(remoteIp, trustedNetworks))
                    context.Request.Headers.Remove("X-Forwarded-Tls-Client-Cert");
            }
            return next(context);
        });

        return app;
    }

    private static bool IsInTrustedNetwork(IPAddress address, List<System.Net.IPNetwork> networks)
    {
        // Normalize IPv4-mapped IPv6 to IPv4
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        foreach (var network in networks)
        {
            if (network.Contains(address))
                return true;
        }
        return false;
    }
}
