namespace Argon.Features;

using System.Reflection.PortableExecutable;
using System.Security.Claims;

public static class HttpContextExtensions
{
    extension(HttpContext ctx)
    {
        public string GetIpAddress()
        {
            // Priority 1: CloudFlare proxy header
            if (ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrWhiteSpace(cfIp))
                return cfIp.ToString();

            // Priority 2: Standard proxy headers (X-Forwarded-For, X-Real-IP)
            if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) && !string.IsNullOrWhiteSpace(forwardedFor))
            {
                // X-Forwarded-For can contain multiple IPs: "client, proxy1, proxy2"
                var ips = forwardedFor.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ips.Length > 0 && !string.IsNullOrWhiteSpace(ips[0]))
                    return ips[0];
            }

            if (ctx.Request.Headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrWhiteSpace(realIp))
                return realIp.ToString();

            // Priority 3: Direct connection IP
            var remoteIp = ctx.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrWhiteSpace(remoteIp))
                return remoteIp;

            // Fallback
            return "unknown";
        }

        public string GetRegion()
            => ctx.Request.Headers.ContainsKey("CF-IPCountry")
                ? ctx.Request.Headers["CF-IPCountry"].ToString()
                : "unknown";

        public string GetRay()
            => ctx.Request.Headers.ContainsKey("CF-Ray")
                ? ctx.Request.Headers["CF-Ray"].ToString()
                : $"{Guid.NewGuid()}";

        public string GetClientName()
            => ctx.Request.Headers.ContainsKey("User-Agent")
                ? ctx.Request.Headers["User-Agent"].ToString()
                : "unknown";

        public Guid GetSessionId()
        {
            // Priority 1: ArgonSecure cookie
            if (ctx.Request.Cookies.TryGetValue("ArgonSecure", out var argonSecure) && !string.IsNullOrWhiteSpace(argonSecure))
            {
                var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(argonSecure);
                if (parsed.TryGetValue("scid", out var scidValue) && Guid.TryParse(scidValue, out var sid))
                    return sid;
            }

            // Priority 2: Legacy headers (fallback for compatibility)
            var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();

            if (env.IsDevelopment() && ctx.Request.Headers.TryGetValue("X-Ctt", out var xCtt) && !string.IsNullOrWhiteSpace(xCtt))
            {
                if (Guid.TryParse(xCtt.ToString(), out var devSid))
                    return devSid;
            }

            if (ctx.Request.Headers.TryGetValue("Sec-Ref", out var secRef) && !string.IsNullOrWhiteSpace(secRef))
            {
                if (Guid.TryParse(secRef.ToString(), out var legacySid))
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

            throw new InvalidOperationException("MachineId is not defined");
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