namespace Argon.Api.Extensions;

public static class HttpContextExtensions
{
    public static string GetIpAddress(this HttpContext ctx)
        => ctx.Request.Headers.ContainsKey("CF-Connecting-IP")
            ? ctx.Request.Headers["CF-Connecting-IP"].ToString()
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static string GetRegion(this HttpContext ctx)
        => ctx.Request.Headers.ContainsKey("CF-IPCountry")
            ? ctx.Request.Headers["CF-IPCountry"].ToString()
            : "unknown";

    public static string GetClientName(this HttpContext ctx)
        => ctx.Request.Headers.ContainsKey("User-Agent")
            ? ctx.Request.Headers["User-Agent"].ToString()
            : "unknown";

    public static string GetHostName(this HttpContext ctx)
        => ctx.Request.Headers.ContainsKey("X-Host-Name")
            ? ctx.Request.Headers["X-Host-Name"].ToString()
            : string.Empty;
}