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

    public static void AddDefaultCors(this WebApplicationBuilder builder)
        => builder.Services.AddCors(x => x.AddDefaultPolicy(z => z.SetIsOriginAllowed(origin =>
            {
                var uri = new Uri(origin);

                return AllowedHost.Any(w =>
                {
                    if (!uri.Scheme.Equals(w.scheme, StringComparison.InvariantCulture))
                        return false;

                    if (uri.Host.Equals(w.host, StringComparison.InvariantCulture))
                        return true;

                    return w.host == "argon.gl" &&
                           (uri.Host.EndsWith(".argon.gl", StringComparison.InvariantCulture));
                });
            })
           .AllowAnyHeader()
           .AllowAnyMethod()
           .AllowCredentials()
           .WithExposedHeaders("X-Wt-Upgrade", "X-Wt-Fingerprint", "X-Wt-AAT")
           .SetPreflightMaxAge(TimeSpan.FromDays(1))));
}