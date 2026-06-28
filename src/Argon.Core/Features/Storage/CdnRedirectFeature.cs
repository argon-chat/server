namespace Argon.Features.Storage;

using Argon.Entities;            // ApplicationDbContext
using Argon.Features;            // HttpContextExtensions.GetRegion
using Argon.Features.Discovery;  // OpenPublicPolicy (AllowAnyOrigin GET CORS)
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
///     Geo-redirect endpoint on the API. Replaces the old region-based URL auto-select: every file URL
///     is region-agnostic and points at this instance's API; the region decision happens here, late and
///     per request, by inspecting the caller's country and 302-ing to the nearest reachable regional
///     mirror.
///
///     <para>Region comes from <see cref="HttpContextExtensions.GetRegion"/> — the same header chain the
///     rest of the app already trusts for IP/geo (CF-IPCountry behind Cloudflare, x-geoip2-country /
///     X-Country behind the geo-aware ingress). When no geo header is present, ResolveTarget falls back
///     to the configured Default.</para>
///
///     <list type="bullet">
///       <item><c>GET /files/{fileId}</c> — resolves the S3 key from the file record (falling back to a
///       flat key == fileId for legacy flat-keyed avatars), then 302s to the regional mirror.</item>
///       <item><c>GET /files/k/{key}</c> — for keyless assets that already know their S3 key (cached
///       GIFs, exports); 302s straight to the regional mirror.</item>
///     </list>
///     Anonymous, because chat media is public (non-presigned) and <c>&lt;img&gt;</c> tags / native
///     fetches can't carry a bearer token — same trust model as the CDN it fronts.
/// </summary>
public static class CdnRedirectFeature
{
    public static WebApplication MapCdnRedirect(this WebApplication app)
    {
        app.MapGet("/files/{fileId:guid}", FileRedirectHandler)
           .AllowAnonymous().RequireCors(DiscoveryFeature.OpenPublicPolicy);

        app.MapGet("/files/k/{**key}", KeyRedirectHandler)
           .AllowAnonymous().RequireCors(DiscoveryFeature.OpenPublicPolicy);

        return app;
    }

    private static async Task<IResult> FileRedirectHandler(
        HttpContext                             ctx,
        Guid                                    fileId,
        IOptions<StorageOptions>                options,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken                       ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var key = await db.Files
           .Where(f => f.Id == fileId && f.Finalized)
           .Select(f => f.S3Key)
           .FirstOrDefaultAsync(ct) ?? fileId.ToString(); // fallback: legacy flat key (key == fileId)

        return RegionRedirect(ctx, options.Value.Cdn, key);
    }

    private static IResult KeyRedirectHandler(HttpContext ctx, string key, IOptions<StorageOptions> options)
        => string.IsNullOrWhiteSpace(key)
            ? Results.NotFound()
            : RegionRedirect(ctx, options.Value.Cdn, key);

    private static IResult RegionRedirect(HttpContext ctx, CdnOptions cdn, string objectKey)
    {
        var country = ctx.GetRegion(); // CF-IPCountry -> x-geoip2-country -> X-Country -> "00" -> Default
        var target  = cdn.BuildRegionalUrl(country, objectKey);

        // The 302 is region-dependent, so it must NEVER be cached by a SHARED cache (a proxy/CDN could
        // hand one region's redirect to a caller in another). Hence `private` — only the end user's own
        // browser may cache it, and that browser stays in one region for the window, so it's safe.
        // (We can't rely on Vary: the geo headers are added by the edge, not sent by the browser, so a
        // browser cache can't vary on them anyway.) max-age trades a brief staleness-after-VPN-change
        // window for fewer redirect round-trips; 0 => no-store (re-evaluate every fetch).
        ctx.Response.Headers.CacheControl = cdn.RedirectCacheSeconds > 0
            ? $"private, max-age={cdn.RedirectCacheSeconds}"
            : "no-store";

        return Results.Redirect(target, permanent: false);
    }
}
