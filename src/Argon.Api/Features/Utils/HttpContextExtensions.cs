namespace Argon.Features;

using System.Security.Claims;

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

    public static string GetRay(this HttpContext ctx)
        => ctx.Request.Headers.ContainsKey("CF-Ray")
            ? ctx.Request.Headers["CF-Ray"].ToString()
            : $"{Guid.NewGuid()}";

    public static string GetClientName(this HttpContext ctx)
        => ctx.Request.Headers.ContainsKey("User-Agent")
            ? ctx.Request.Headers["User-Agent"].ToString()
            : "unknown";

    public static string GetHostName(this HttpContext ctx)
        => ctx.Request.Headers.ContainsKey("X-Host-Name")
            ? ctx.Request.Headers["X-Host-Name"].ToString()
            : string.Empty;

    public static Guid GetUserId(this HttpContext ctx)
    {
        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

        userId ??= ctx.User.FindFirstValue("id");

        if (Guid.TryParse(userId, out var result))
            return result;
        throw new FormatException($"UserId by '{ClaimTypes.NameIdentifier} claim has value: '{userId}' - incorrect guid");
    }
}