namespace Argon.Features.Middlewares;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

/// <summary>
/// Serving the <c>.br</c> or <c>.gz</c> the build already produced, instead of the file itself.
/// </summary>
/// <remarks>
/// <para>A front-end bundle is three to four times its own size on the wire if nothing compresses
/// it, and nothing here does: Kestrel has no response compression configured, and the proxy in
/// front does not add any. So the widget's build writes a brotli and a gzip sibling next to every
/// asset worth compressing, and this rewrites the request path onto whichever one the caller said
/// it would accept.</para>
///
/// <para>Compressing at build time rather than per request is the whole point. These files are
/// named by content hash and never change, so there is exactly one right moment to spend the CPU,
/// and brotli at maximum quality — far too slow to do per request — is affordable there.</para>
///
/// <para>Only the path is rewritten. <see cref="PrecompressedContentTypeProvider"/> is what stops
/// the static-file middleware from then deciding that <c>index-a1b2c3.js.br</c> is a file of
/// unknown type and declining to serve it.</para>
/// </remarks>
public static class PrecompressedStaticFilesExtensions
{
    /// <summary>Best first: brotli wins wherever it is offered.</summary>
    private static readonly (string Encoding, string Extension)[] Encodings =
    [
        ("br", ".br"),
        ("gzip", ".gz")
    ];

    public static WebApplication UsePrecompressedStaticFiles(this WebApplication app, IFileProvider files)
    {
        app.Use(async (http, next) =>
        {
            if (TryNegotiate(http, files))
            {
                // Without this a shared cache can hand a brotli body to a client that never asked
                // for one, which reads as a corrupt file rather than as a caching bug.
                http.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
            }

            await next();
        });

        return app;
    }

    private static bool TryNegotiate(HttpContext http, IFileProvider files)
    {
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            return false;

        // A path that already carries a Content-Encoding is one somebody else is answering.
        if (http.Response.Headers.ContentEncoding.Count > 0)
            return false;

        if (http.Request.Path.Value is not { Length: > 1 } path)
            return false;

        // Asked for by its real name. The bytes are compressed either way, so the only question is
        // whether the answer says so — and an answer that does not is a script no browser can
        // parse. PrecompressedContentTypeProvider will have given it the underlying file's type,
        // which is exactly half of the pair; this is the other half.
        foreach (var (encoding, extension) in Encodings)
        {
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;

            http.Response.Headers.ContentEncoding = encoding;
            return true;
        }

        var accepted = http.Request.Headers.AcceptEncoding;
        if (accepted.Count == 0)
            return false;

        foreach (var (encoding, extension) in Encodings)
        {
            if (!Accepts(accepted, encoding))
                continue;

            if (files.GetFileInfo(path + extension) is not { Exists: true, IsDirectory: false })
                continue;

            http.Response.Headers.ContentEncoding = encoding;
            http.Request.Path                     = path + extension;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the client offered this encoding, without treating <c>br;q=0</c> as an offer.
    /// </summary>
    /// <remarks>
    /// A plain substring test would also match <c>x-brotli</c> or an encoding the client explicitly
    /// refused, and answering with a body nothing can decode is worse than answering uncompressed.
    /// </remarks>
    private static bool Accepts(StringValues acceptEncoding, string encoding)
    {
        foreach (var header in acceptEncoding)
        {
            if (header is null)
                continue;

            foreach (var candidate in header.Split(','))
            {
                var span  = candidate.AsSpan().Trim();
                var comma = span.IndexOf(';');
                var name  = (comma < 0 ? span : span[..comma]).Trim();

                if (!name.Equals(encoding, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (comma >= 0 && span[comma..].Contains("q=0", StringComparison.OrdinalIgnoreCase) &&
                    !span[comma..].Contains("q=0.", StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The content type of <c>index-a1b2c3.js.br</c> is the content type of <c>index-a1b2c3.js</c>.
/// </summary>
/// <remarks>
/// The static-file middleware looks the type up from the path it is about to serve, which by then
/// is the compressed sibling. Left alone it would find no mapping for <c>.br</c> and — with
/// <c>ServeUnknownFileTypes</c> off, as it should be — decline to serve the file at all. Stripping
/// the compression extension before the lookup is what makes the rewrite invisible to the client:
/// it receives <c>Content-Type: text/javascript</c> with <c>Content-Encoding: br</c>, which is the
/// pair a browser expects.
/// </remarks>
public sealed class PrecompressedContentTypeProvider : IContentTypeProvider
{
    private readonly FileExtensionContentTypeProvider inner = new();

    public bool TryGetContentType(string subpath, out string contentType)
    {
        if (subpath.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ||
            subpath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            subpath = subpath[..subpath.LastIndexOf('.')];

        return inner.TryGetContentType(subpath, out contentType!);
    }
}
