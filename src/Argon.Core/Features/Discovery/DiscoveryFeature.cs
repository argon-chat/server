namespace Argon.Features.Discovery;

using Argon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class DiscoveryFeature
{
    /// <summary>
    /// CORS policy for the public, anonymous discovery endpoints. They are read-only config with no
    /// credentials, so they must be reachable from ANY origin (arbitrary self-hosted web clients and
    /// the official web client). AllowAnyOrigin is incompatible with AllowCredentials — hence a
    /// dedicated named policy, kept separate from the credentialed default allowlist (CorsFeature).
    /// </summary>
    public const string OpenPublicPolicy = "OpenPublicDiscovery";

    public static WebApplicationBuilder AddDiscoveryFeature(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
        builder.Services.AddCors(o => o.AddPolicy(OpenPublicPolicy, p =>
            p.AllowAnyOrigin()
             .AllowAnyHeader()
             .WithMethods("GET", "OPTIONS")));
        return builder;
    }

    public static WebApplication MapDiscovery(this WebApplication app)
    {
        // Instance manifest — served by EVERY instance so self-hosters get discovery for free.
        // Fetched by the client over plain HTTP before any RPC client exists.
        app.MapGet("/.well-known/argon-instance.json", ManifestHandler)
           .AllowAnonymous().RequireCors(OpenPublicPolicy);
        // Proxy/CDN-friendly alias.
        app.MapGet("/api/instance/manifest", ManifestHandler)
           .AllowAnonymous().RequireCors(OpenPublicPolicy);

        // Enterprise/SaaS routing — maps an email domain to a managed instance URL.
        // Only meaningful on the main directory server; on other instances the table is simply empty.
        app.MapGet("/api/discovery/resolve", ResolveHandler)
           .AllowAnonymous().RequireCors(OpenPublicPolicy);

        return app;
    }

    private static IResult ManifestHandler(IOptions<DiscoveryOptions> options)
    {
        var m = options.Value.Manifest;
        return Results.Ok(new InstanceManifestDto(
            SchemaVersion: m.SchemaVersion,
            Instance: new ManifestInstanceDto(m.InstanceId, m.DisplayName, m.Kind),
            Endpoints: new ManifestEndpointsDto(m.ApiUrl, m.CdnUrl),
            Branding: new ManifestBrandingDto(m.DisplayName, m.LogoUrl, m.AccentColor),
            Features: new ManifestFeaturesDto(m.RegistrationEnabled, m.QrLoginEnabled, m.SsoUrl),
            Legal: new ManifestLegalDto(m.TermsUrl, m.PrivacyUrl),
            MinClientVersion: m.MinClientVersion));
    }

    private static async Task<IResult> ResolveHandler(
        string?                                 email,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IArgonCacheDatabase                     cache,
        ILoggerFactory                          loggerFactory,
        CancellationToken                       ct)
    {
        var domain = ExtractDomain(email);
        // Invalid / missing domain → treat as official (never enumerate, never error).
        if (domain is null)
            return Results.Ok(new ResolveResultDto("official", null));

        // Per-domain throttle. Fail-OPEN so a directory outage never blocks enterprise sign-in.
        try
        {
            var key   = $"rl:discovery:resolve:{domain}";
            var count = await cache.StringIncrementAsync(key, ct);
            if (count == 1)
                await cache.KeyExpireAsync(key, TimeSpan.FromMinutes(5), ct);
            if (count > 30)
                return Results.Ok(new ResolveResultDto("official", null));
        }
        catch
        {
            // ignore — fall through to the lookup
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.TenantDirectory
               .AsNoTracking()
               .Where(t => t.Domain == domain && t.IsVerified && !t.IsDeleted)
               .Select(t => new { t.InstanceUrl })
               .FirstOrDefaultAsync(ct);

            return Results.Ok(row is null
                ? new ResolveResultDto("official", null)
                : new ResolveResultDto("managed", row.InstanceUrl));
        }
        catch (Exception e)
        {
            loggerFactory.CreateLogger("Discovery").LogWarning(e, "tenant resolve failed; failing open to official");
            return Results.Ok(new ResolveResultDto("official", null));
        }
    }

    /// <summary>Lower-cased domain after the last '@', or null when the input isn't a plausible email.</summary>
    private static string? ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        // Minimal host sanity: must contain a dot and no whitespace/control chars.
        if (domain.Length == 0 || !domain.Contains('.') || domain.Any(char.IsWhiteSpace))
            return null;
        return domain;
    }
}
