namespace Argon.Features.Web;

public static class CorsFeature
{
    public static List<(string scheme, string host)> AllowedHost =
    [
        ("http" , "localhost"),                       // for testing
        ("https", "localhost"),                       // for testing
        ("app"  , "index"),                           // origin in host
        ("https", "argon.gl"),                        // base domain
        ("https", "argon.zone"),                      // extended apps
        ("https", "link.argon.gl"),                   // link app
        ("https", "meet.argon.gl"),                   // meet
        ("https", "aegis.argon.gl"),                  // identity
        ("https", "console.argon.gl"),                // identity
        ("https", "x-frontend-development.argon.gl"), // for testing
        ("https", "local.argon.gl")                   // for testing
    ];

    public static void AddDefaultCors(this WebApplicationBuilder builder)
        => builder.Services.AddCors(x => x.AddDefaultPolicy(z => z.SetIsOriginAllowed(origin =>
            {
                var uri = new Uri(origin);
                return AllowedHost.Any(w =>
                    uri.Host.Equals(w.host, StringComparison.InvariantCulture) &&
                    uri.Scheme.Equals(w.scheme, StringComparison.InvariantCulture));
            })
           .AllowAnyHeader()
           .AllowAnyMethod()
           .WithExposedHeaders("X-Wt-Upgrade", "X-Wt-Fingerprint", "X-Wt-AAT")
           .SetPreflightMaxAge(TimeSpan.FromDays(1))));
}