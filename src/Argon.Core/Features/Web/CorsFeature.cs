namespace Argon.Features.Web;

public static class CorsFeature
{
    public static List<(string scheme, string host)> AllowedHost =
    [
        ("http", "localhost"),
        ("https", "localhost"),

        ("app", "index"),
        ("https", "app"),

        ("https", "argon.gl"),
        ("https", "argon.zone"),

        ("https", "link.argon.gl"),
        ("https", "meet.argon.gl"),
        ("https", "aegis.argon.gl"),
        ("https", "console.argon.gl"),
        ("https", "argx.argon.gl"),
        ("https", "k3sd.argon.gl"),
        ("https", "vault.argon.gl"),
        ("https", "x-frontend-development.argon.gl"),
        ("https", "local.argon.gl"),

        ("https", "www.jwt.io"),
        ("https", "jwt.io"),

    ];

    /// <summary>
    /// Turns a configured <c>scheme://host</c> list into the pair form the matcher uses. Anything
    /// unparseable is dropped here; the feature's validation rule is what reports it.
    /// </summary>
    public static List<(string scheme, string host)> Parse(IEnumerable<string> origins)
        => origins
           .Select(o => Uri.TryCreate(o, UriKind.Absolute, out var uri) ? (uri.Scheme, uri.Host) : default)
           .Where(pair => pair.Scheme is not null)
           .ToList();

    public static void AddDefaultCors(this WebApplicationBuilder builder, IReadOnlyList<string>? origins = null)
    {
        var allowed = origins is { Count: > 0 } ? Parse(origins) : AllowedHost;

        builder.Services.AddCors(x => x.AddDefaultPolicy(z => z.SetIsOriginAllowed(origin =>
            {
                try
                {
                    var uri = new Uri(origin);

                    return allowed.Any(w =>
                    {
                        if (!uri.Scheme.Equals(w.scheme, StringComparison.InvariantCulture))
                            return false;

                        if (uri.Host.Equals(w.host, StringComparison.InvariantCulture))
                            return true;

                        return w.host == "argon.gl" &&
                               (uri.Host.EndsWith(".argon.gl", StringComparison.InvariantCulture));
                    });
                }
                catch (UriFormatException)
                {
                    return false;
                }
            })
           .AllowAnyHeader()
           .AllowAnyMethod()
           .AllowCredentials()
           .WithExposedHeaders("X-Wt-Upgrade", "X-Wt-Fingerprint", "X-Wt-AAT")
           .SetPreflightMaxAge(TimeSpan.FromDays(1))));
    }
}