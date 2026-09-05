namespace Argon.Features;

using System.Security.Claims;
using Argon.Features.Middlewares;

public static class HttpContextExtensions
{
    extension(HttpContext ctx)
    {
        /// <summary>
        /// Which machine this request came from, as far as it can be established.
        /// </summary>
        /// <remarks>
        /// <para>The header ladder below is only consulted when the hop that delivered the request is a
        /// proxy this process trusts. Every rung of it is a header, so a caller connecting directly can
        /// set any of them and be recorded as any address it likes — and this value is what the
        /// anonymous rate limits count, what the captcha is verified against, and what lands in the
        /// session record a user later reads as "where was I signed in from". Reading them
        /// unconditionally made all three of those the caller's choice.</para>
        ///
        /// <para>Untrusted hop means the peer is the caller, so the peer address is the answer — which
        /// is also what a direct connection has always returned, by falling through the ladder.</para>
        /// </remarks>
        public string GetIpAddress()
        {
            if (!ctx.ArrivedThroughTrustedProxy())
                return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Priority 1: CloudFlare proxy header
            if (ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrWhiteSpace(cfIp))
                return cfIp.ToString();

            // Priority 2: MaxMind GeoIP2 (nginx/ingress module)
            if (ctx.Request.Headers.TryGetValue("x-geoip2-ipaddress", out var geoIp) && !string.IsNullOrWhiteSpace(geoIp))
                return geoIp.ToString();

            // Priority 3: Standard proxy headers (X-Forwarded-For, X-Real-IP)
            if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) && !string.IsNullOrWhiteSpace(forwardedFor))
            {
                // X-Forwarded-For can contain multiple IPs: "client, proxy1, proxy2"
                var ips = forwardedFor.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ips.Length > 0 && !string.IsNullOrWhiteSpace(ips[0]))
                    return ips[0];
            }

            if (ctx.Request.Headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrWhiteSpace(realIp))
                return realIp.ToString();

            // Priority 4: Direct connection IP
            var remoteIp = ctx.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrWhiteSpace(remoteIp))
                return remoteIp;

            // Fallback
            return "unknown";
        }

        /// <summary>
        /// The country the edge placed this caller in, or <c>"00"</c> when nothing did.
        /// </summary>
        /// <remarks>
        /// Gated on the same trust as <see cref="GetIpAddress"/>, and for the same reason: these are
        /// headers only an edge is supposed to write. Unknown is the honest answer for a request that
        /// did not come through one, and it is the answer such a request already got.
        /// </remarks>
        public string GetRegion() => ctx.GetGeoLocation().Country;

        /// <summary>
        /// Country, region and city the edge placed this caller in, with every edge's "don't know"
        /// placeholder already folded into unknown.
        /// </summary>
        /// <remarks>
        /// <para>Same trust gate as <see cref="GetRegion"/>. Cloudflare is read first, then Traefik's
        /// geoip2 plugin, then a bare <c>X-Country</c>; the parts are always taken from the same edge as
        /// the country, so a city is never paired with another hop's country.</para>
        ///
        /// <para>The plugin only fills region and city when it was given a City database — with the
        /// Country edition both arrive as <c>XX</c>, which <see cref="GeoLocation.Of"/> reads as
        /// unknown. Cloudflare needs the "add visitor location headers" managed transform switched on
        /// for anything beyond the country.</para>
        /// </remarks>
        public Features.Auth.GeoLocation GetGeoLocation()
        {
            if (!ctx.ArrivedThroughTrustedProxy())
                return Features.Auth.GeoLocation.Unknown;

            var headers = ctx.Request.Headers;

            string? First(params string[] names)
            {
                foreach (var name in names)
                {
                    if (headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                        return value.ToString();
                }

                return null;
            }

            if (First("CF-IPCountry") is { } cloudflare)
                return Features.Auth.GeoLocation.Of(cloudflare, First("cf-region", "cf-region-code"), First("cf-ipcity"));

            if (First("x-geoip2-country") is { } geoip)
                return Features.Auth.GeoLocation.Of(geoip, First("x-geoip2-region"), First("x-geoip2-city"));

            return Features.Auth.GeoLocation.Of(First("X-Country"), null, null);
        }

        public string GetRay()
            => ctx.Request.Headers.ContainsKey("CF-Ray")
                ? ctx.Request.Headers["CF-Ray"].ToString()
                : $"{ArgonId.New()}";

        public string GetClientName()
            => ctx.Request.Headers.ContainsKey("User-Agent")
                ? ctx.Request.Headers["User-Agent"].ToString()
                : "unknown";

        /// <summary>
        /// What the client says it is: the <c>X-Argon-Client</c> header a first-party client sends,
        /// filled out from the User-Agent for everything it leaves unsaid. Display-only.
        /// </summary>
        public Features.Auth.ClientDescriptor GetClientDescriptor()
            => Features.Auth.ClientDescriptor.From(
                ctx.Request.Headers.TryGetValue(Features.Auth.ClientDescriptor.HeaderName, out var declared) ? declared.ToString() : null,
                ctx.Request.Headers.TryGetValue("User-Agent", out var userAgent) ? userAgent.ToString() : null);

        // The client's current app locale (raw app code, e.g. "ru", "jp", "ru_pt").
        // Normalized to BCP-47 at the Bot API boundary via LocaleNormalizer.
        public string? GetClientLocale()
            => ctx.Request.Headers.TryGetValue("x-argon-locale", out var locale) && !string.IsNullOrWhiteSpace(locale)
                ? locale.ToString()
                : null;

        public Guid GetSessionId()
        {
            if (ctx.Request.Cookies.TryGetValue("ArgonSecure", out var argonSecure) && !string.IsNullOrWhiteSpace(argonSecure))
            {
                var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(argonSecure);
                if (parsed.TryGetValue("scid", out var scidValue) && Guid.TryParse(scidValue, out var sid))
                    return sid;
            }

            var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();

            if (env.IsDevelopment())
            {
                if (ctx.Request.Headers.TryGetValue("X-Ctt", out var xCtt) && !string.IsNullOrWhiteSpace(xCtt)
                    && Guid.TryParse(xCtt.ToString(), out var devSid))
                    return devSid;
                return Guid.AllBitsSet;
            }

            // Priority 2: Legacy headers (fallback for compatibility)
            if (ctx.Request.Headers.TryGetValue("Sec-Ref", out var secRef) && !string.IsNullOrWhiteSpace(secRef))
            {
                if (Guid.TryParse(secRef.ToString(), out var legacySid))
                    return legacySid;
                throw new InvalidOperationException("SessionId invalid");
            }

            if (ctx.Request.Headers.TryGetValue("X-Sec-Ref", out var xSecRef) && !string.IsNullOrWhiteSpace(xSecRef))
            {
                if (Guid.TryParse(xSecRef.ToString(), out var legacySid))
                    return legacySid;
                throw new InvalidOperationException("SessionId invalid");
            }

            throw new InvalidOperationException("SessionId is not defined");
        }

        public bool TryGetSessionId(out Guid sessionId)
        {
            try
            {
                sessionId = ctx.GetSessionId();
                return true;
            }
            catch
            {
                sessionId = Guid.Empty;
                return false;
            }
        }

        /// <summary>
        /// Which machine this request came from.
        /// </summary>
        /// <remarks>
        /// <para>The development constant below used to be checked <em>first</em>, which made every
        /// caller on a development host the same machine. That is not a test-only inconvenience:
        /// the machine identity is what binds a QR sign-in code to the browser that asked for it,
        /// and what the <c>mh</c> claim on every access token is checked against. Collapsing it to a
        /// constant turns both of those into no-ops on any deployment running in Development.</para>
        ///
        /// <para>So it is a fallback now rather than an override: a caller that presents a machine
        /// identity gets its own, and the constant only stands in for callers that present none —
        /// which is the case the constant existed for, since a local host has no
        /// <c>ArgonSecure</c> cookie to read.</para>
        /// </remarks>
        public string GetMachineId()
        {
            // Priority 1: ArgonSecure cookie
            if (ctx.Request.Cookies.TryGetValue("ArgonSecure", out var argonSecure) && !string.IsNullOrWhiteSpace(argonSecure))
            {
                var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(argonSecure);
                if (parsed.TryGetValue("colt", out var coltValue) && !string.IsNullOrWhiteSpace(coltValue))
                    return coltValue.ToString();
            }

            // Priority 2: Legacy header (fallback for compatibility)
            if (ctx.Request.Headers.TryGetValue("Sec-Carry", out var secCarry) && !string.IsNullOrWhiteSpace(secCarry))
            {
                var machineId = secCarry.ToString();
                if (!string.IsNullOrWhiteSpace(machineId))
                    return machineId;
                throw new InvalidOperationException("MachineId invalid");
            }

            // Priority 3: Alternate legacy header (fallback for compatibility)
            if (ctx.Request.Headers.TryGetValue("X-Sec-Carry", out var xSecCarry) && !string.IsNullOrWhiteSpace(xSecCarry))
            {
                var machineId = xSecCarry.ToString();
                if (!string.IsNullOrWhiteSpace(machineId))
                    return machineId;
                throw new InvalidOperationException("MachineId invalid");
            }

            // Nothing identified the caller. Locally that is ordinary — there is no ArgonSecure
            // cookie on a dev host — so stand one in rather than failing every request. Anywhere
            // else an unidentified caller is a caller we cannot bind a token to.
            if (ctx.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
                return "1234";

            throw new InvalidOperationException("MachineId is not defined");
        }

        /// <summary>
        /// The hardware signals this caller reported, or an empty vector.
        /// </summary>
        /// <remarks>
        /// Only the <c>ArgonSecure</c> cookie carries it — there is no legacy header fallback,
        /// because there is no legacy format: a client old enough to lack the field reports nothing
        /// and is scored as an unknown device rather than refused.
        /// </remarks>
        public Features.Auth.DeviceFingerprint GetHardwareVector()
        {
            if (!ctx.Request.Cookies.TryGetValue("ArgonSecure", out var argonSecure) || string.IsNullOrWhiteSpace(argonSecure))
                return Features.Auth.DeviceFingerprint.Empty;

            var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(argonSecure);

            return parsed.TryGetValue("hwv", out var hwv)
                ? ctx.RequestServices.GetRequiredService<Features.Auth.DeviceMatcher>()
                     .Parse(Uri.UnescapeDataString(hwv.ToString()))
                : Features.Auth.DeviceFingerprint.Empty;
        }

        /// <summary>
        /// The device proof this request carries, or null.
        /// </summary>
        /// <remarks>
        /// <para>Two channels, one format (<c>publicKey.issuedAt.signature[.attestation]</c>). The
        /// <c>Sec-Proof</c> header is a proof made for this very call: the desktop asks its TPM to sign
        /// right before the handful of calls that mint or refresh a session, so the one-minute window
        /// the verifier allows is never a problem. The cookie's <c>dev</c> field is what native code
        /// wrote at launch — good for a refresh within a minute of starting and nothing after, which is
        /// all an older desktop build can offer.</para>
        ///
        /// <para>Neither channel appears in the ion contract, deliberately: hardware identity rides
        /// beside the request, and a client too old to send either reports nothing and is judged on the
        /// fingerprint vector instead.</para>
        /// </remarks>
        public string? GetDeviceProof()
        {
            if (ctx.Request.Headers.TryGetValue("Sec-Proof", out var fresh) && !string.IsNullOrWhiteSpace(fresh))
                return fresh.ToString().Trim();

            if (!ctx.Request.Cookies.TryGetValue("ArgonSecure", out var argonSecure) || string.IsNullOrWhiteSpace(argonSecure))
                return null;

            var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(argonSecure);

            return parsed.TryGetValue("dev", out var dev) && !string.IsNullOrWhiteSpace(dev)
                ? Uri.UnescapeDataString(dev.ToString())
                : null;
        }

        public bool TryGetMachineId(out string machineId)
        {
            try
            {
                machineId = ctx.GetMachineId();
                return true;
            }
            catch
            {
                machineId = string.Empty;
                return false;
            }
        }

        public Guid GetUserId()
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

            userId ??= ctx.User.FindFirstValue("id");

            if (Guid.TryParse(userId, out var result))
                return result;
            throw new FormatException($"UserId by '{ClaimTypes.NameIdentifier} claim has value: '{userId}' - incorrect guid");
        }


        public string GetAppId()
        {
            if (ctx.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
                return "1234";
            // Priority 1: ArgonSecure cookie
            if (ctx.Request.Cookies.TryGetValue("ArgonSecure", out var argonSecure) && !string.IsNullOrWhiteSpace(argonSecure))
            {
                var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(argonSecure);
                if (parsed.TryGetValue("ner", out var nerValue) && !string.IsNullOrWhiteSpace(nerValue))
                    return nerValue.ToString();
            }

            // Priority 2: Legacy header (fallback for compatibility)
            if (ctx.Request.Headers.TryGetValue("Sec-Ner", out var secNer) ||
                ctx.Request.Headers.TryGetValue("X-Sec-Ner", out secNer))
            {
                var appId = secNer.ToString();
                if (!string.IsNullOrWhiteSpace(appId))
                    return appId;
                throw new InvalidOperationException("AppId invalid");
            }

            throw new InvalidOperationException("AppId is not defined");
        }

        public bool TryGetAppId(out string appId)
        {
            try
            {
                appId = ctx.GetAppId();
                return true;
            }
            catch
            {
                appId = string.Empty;
                return false;
            }
        }
    }
}