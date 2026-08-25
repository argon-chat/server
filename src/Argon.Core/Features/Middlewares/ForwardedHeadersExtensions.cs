namespace Argon.Features.Middlewares;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;

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

        // Runs before the rewrite, because after it the peer address is gone: the middleware replaces
        // RemoteIpAddress with whatever the headers claimed, so this is the last point at which anyone
        // can still see which machine actually opened the connection. That is the only fact that makes
        // the headers evidence rather than user input, and code further in needs to know it — see
        // ProxyTrust.
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Argon.ForwardedHeaders");

        app.Use(async (context, next) =>
        {
            var trusted = ProxyTrust.Evaluate(context, options.KnownIPNetworks, options.KnownProxies);

            if (!trusted && context.Request.Headers.ContainsKey("X-Forwarded-For"))
                WarnAboutUntrustedHop(logger, context.Connection.RemoteIpAddress);

            await next(context);
        });

        app.UseForwardedHeaders(options);

        return app;
    }

    private static long lastWarning;

    /// <summary>
    /// Says out loud that forwarded headers arrived from a machine this process does not trust.
    /// </summary>
    /// <remarks>
    /// Worth a line in the log because the failure it reports is invisible from the outside: every
    /// request keeps working, and every one of them is attributed to the proxy. Anonymous rate limits
    /// then count one shared bucket instead of one per caller, so the first person to trip a limit
    /// trips it for everyone. That reads as a load problem rather than as a misconfigured CIDR list.
    ///
    /// Throttled to one line every five minutes: it fires per request, and a proxy outside the known
    /// ranges means every request, which would bury the log it is meant to draw attention to.
    /// </remarks>
    private static void WarnAboutUntrustedHop(ILogger logger, IPAddress? peer)
    {
        var now  = Environment.TickCount64;
        var last = Interlocked.Read(ref lastWarning);

        if (last != 0 && now - last < 5 * 60 * 1000)
            return;

        if (Interlocked.CompareExchange(ref lastWarning, now, last) != last)
            return;

        logger.LogWarning(
            "Request from {Peer} carried X-Forwarded-For, but {Peer} is not a known proxy — the headers " +
            "are being ignored and every caller behind it is counted as one. Add its address or network " +
            "to ForwardedHeaders:KnownProxies / ForwardedHeaders:KnownNetworks.",
            peer, peer);
    }
}

/// <summary>
/// Whether a request reached this process through a hop that is allowed to speak for its caller.
/// </summary>
/// <remarks>
/// <c>X-Forwarded-For</c>, <c>CF-Connecting-IP</c> and the rest are headers: any client can write them,
/// and a client talking to us directly can write whatever it likes. They become evidence only when the
/// machine that delivered them is one that overwrites rather than appends — which is what
/// <see cref="ArgonForwardedHeadersOptions"/> enumerates and what this records per request.
///
/// Absent marking, the answer is no. A role that never enabled the forwarded-headers feature has no
/// proxy in front of it as far as it knows, so believing a header there would be believing the caller.
/// </remarks>
public static class ProxyTrust
{
    private const string ItemKey = "argon.forwarded-headers.trusted";

    /// <summary>
    /// Decides whether this request's forwarded headers may be believed, and records the answer for the
    /// rest of the pipeline. Returns what it recorded.
    /// </summary>
    /// <remarks>
    /// Deciding and recording are one call on purpose: there is no way to assert trust without having
    /// established it, and no second copy of the rule to fall out of step with the first.
    ///
    /// Has to run before <c>UseForwardedHeaders</c>. That middleware overwrites
    /// <c>RemoteIpAddress</c> with what the headers claimed, and the peer address is the whole input
    /// here — read afterwards, the question would be answered with the answer.
    /// </remarks>
    public static bool Evaluate(HttpContext context, IList<System.Net.IPNetwork> knownNetworks, IList<IPAddress> knownProxies)
    {
        var peer    = context.Connection.RemoteIpAddress;
        var trusted = peer is not null && IsTrustedHop(peer, knownNetworks, knownProxies);

        context.Items[ItemKey] = trusted;

        return trusted;
    }

    public static bool ArrivedThroughTrustedProxy(this HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var trusted) && trusted is true;

    private static bool IsTrustedHop(IPAddress peer, IList<System.Net.IPNetwork> knownNetworks, IList<IPAddress> knownProxies)
    {
        // A v4 proxy reaching a dual-stack listener arrives as ::ffff:10.42.0.9, and this is what makes
        // that peer comparable to an operator who wrote 10.42.0.9 in KnownProxies: IPAddress.Equals
        // compares the two forms as different addresses. (IPNetwork.Contains does unmap, so the CIDR
        // branch below would work without this — the framework's own middleware unmaps for the same
        // reason, in the same place.) Getting it wrong reports nothing: a correctly configured
        // deployment keeps serving, with every caller collapsed into the proxy's identity.

        var unmapped = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;

        foreach (var proxy in knownProxies)
        {
            var known = proxy.IsIPv4MappedToIPv6 ? proxy.MapToIPv4() : proxy;
            if (known.Equals(unmapped))
                return true;
        }

        foreach (var network in knownNetworks)
        {
            if (network.Contains(peer))
                return true;
        }

        return false;
    }
}
