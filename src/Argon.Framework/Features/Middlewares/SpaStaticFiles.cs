namespace Argon.Features.Middlewares;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

/// <summary>
/// Serving a built single-page application out of a directory.
/// </summary>
/// <remarks>
/// <para>Two roles do this — the identity server serves the sign-in widget, the account role serves
/// the developer console — and the four decisions involved are the same both times: prefer the
/// compressed sibling the build wrote, name the content type from the uncompressed file, cache the
/// hashed assets forever and the document that names them never, and answer a client-routed path
/// with the shell rather than a 404. See <c>src/Frontend</c> for the other end of all four.</para>
///
/// <para>Ordering is the part that does not survive being written twice from memory. The
/// precompression rewrite has to run before <c>UseStaticFiles</c> and after whatever security
/// headers the role adds; the fallback has to be a routed endpoint, so it is mapped rather than
/// used; and <c>UseDefaultFiles</c> earns its place only for a request that reaches it with no
/// endpoint selected.</para>
/// </remarks>
public static class SpaStaticFilesExtensions
{
    /// <summary>
    /// Serves <paramref name="staticRoot"/> as a single-page application, or does nothing at all
    /// when it is empty — which is what a deployment that puts the front-end behind its own CDN
    /// configures.
    /// </summary>
    public static WebApplication UseSpaStaticFiles(this WebApplication app, string? staticRoot)
    {
        if (string.IsNullOrWhiteSpace(staticRoot))
            return app;

        var root  = Path.GetFullPath(staticRoot);
        var files = new PhysicalFileProvider(root);

        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider     = files,
            DefaultFileNames = ["index.html"]
        });

        app.UsePrecompressedStaticFiles(files);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider          = files,
            ServeUnknownFileTypes = false,
            ContentTypeProvider   = new PrecompressedContentTypeProvider(),

            OnPrepareResponse = context =>
            {
                // Everything under /assets is named by content hash, so a change is a new URL and
                // the old one can be kept forever. index.html is the opposite: it is the document
                // that names those hashes, and a cached copy of it points at a build that may no
                // longer exist.
                context.Context.Response.Headers.CacheControl =
                    context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                        ? "no-cache"
                        : "public, max-age=31536000, immutable";
            }
        });

        // The app routes client-side, so a deep link has to be answered with the shell rather than
        // a 404. MapFallback's pattern excludes paths that look like files, which is what keeps a
        // missing asset a 404 instead of a page.
        app.MapFallback(async http =>
        {
            http.Response.ContentType  = "text/html";
            http.Response.Headers.CacheControl = "no-cache";

            await http.Response.SendFileAsync(Path.Combine(root, "index.html"));
        });

        return app;
    }
}
