namespace Argon.Features.BotApi;

using System.Text.RegularExpressions;

/// <summary>
/// Middleware that supports path-based bot authentication:
/// <c>/api/bot/{token}/IMessages/v1/Send</c> → extracts token into
/// <c>Authorization: Bot {token}</c> header and rewrites path to
/// <c>/api/bot/IMessages/v1/Send</c> so routing works normally.
/// </summary>
public sealed class BotPathTokenMiddleware(RequestDelegate next)
{
    // Match bot tokens (HEX:BASE64URL) after /api/bot/ — interface names start with "I" so no ambiguity
    // Colon may appear as literal ':' or URL-encoded '%3A'
    private static readonly Regex PathTokenPattern =
        new(@"^/api/bot/([a-fA-F0-9]{32}(?::|%3[Aa])[A-Za-z0-9_\-]{20,200})(/.*)", RegexOptions.Compiled);

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith("/api/bot/"))
            return next(context);

        var match = PathTokenPattern.Match(path);
        if (!match.Success)
            return next(context);

        var token    = Uri.UnescapeDataString(match.Groups[1].Value);
        var restPath = match.Groups[2].Value; // e.g. "/IMessages/v1/Send"

        // Set Authorization header so the auth handler picks it up
        context.Request.Headers["Authorization"] = $"Bot {token}";
        // Rewrite path for routing
        context.Request.Path = $"/api/bot{restPath}";

        return next(context);
    }
}

public static class BotPathTokenExtensions
{
    /// <summary>
    /// Adds support for <c>/api/bot/{token}/Interface/vN/Method</c> path-based authentication.
    /// Must be called before <c>UseAuthentication()</c> and <c>UseRouting()</c>.
    /// </summary>
    public static IApplicationBuilder UseBotPathTokenAuth(this IApplicationBuilder app)
        => app.UseMiddleware<BotPathTokenMiddleware>();
}
