namespace Argon.Features.Web;

public static class CorsFeature
{
    public static List<(string scheme, string host)> AllowedHost =
    [
        ("http", "localhost"),
        ("https", "localhost"),
        ("app", "index"),
        ("https", "argon.gl"),
        ("https", "argon.zone"),
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
           .WithExposedHeaders("X-Wt-Upgrade", "X-Wt-Fingerprint", "X-Wt-AAT")));
}

/*context.Response.Headers.TryAdd("X-Wt-Upgrade", opt.Value.Upgrade);
if (!string.IsNullOrEmpty(opt.Value.CertificateFingerprint))
    context.Response.Headers.TryAdd("X-Wt-Fingerprint", opt.Value.CertificateFingerprint);
context.Response.Headers.TryAdd("X-Wt-AAT", aat.ToString());*/