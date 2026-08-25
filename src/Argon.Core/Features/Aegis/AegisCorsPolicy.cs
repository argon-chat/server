namespace Argon.Features.Aegis;

using Argon.Features.Web;

/// <summary>
/// Whether a browser origin may talk to the identity server.
/// </summary>
/// <remarks>
/// Two lists, and the second is why this is a service rather than a policy handed to
/// <c>AddCors</c>. Argon's own hosts are static and known at build time; every other allowed origin
/// is a site somebody registered a redirect for, which means the answer lives in the database and
/// changes without a deployment. ASP.NET's origin predicate is synchronous, so the dynamic half is
/// checked in middleware and the registered policy waves everything through — see
/// <c>AegisFeature</c>, where the two halves are wired together.
/// </remarks>
public sealed class AegisCorsPolicy(IAegisDirectory directory)
{
    public async Task<bool> IsAllowedAsync(string origin, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (IsArgonHost(uri))
            return true;

        var registered = await directory.GetAllowedOriginsAsync(ct);

        return registered.Contains(origin);
    }

    private static bool IsArgonHost(Uri uri)
        => CorsFeature.AllowedHost.Any(allowed =>
        {
            if (!uri.Scheme.Equals(allowed.scheme, StringComparison.InvariantCulture))
                return false;

            if (uri.Host.Equals(allowed.host, StringComparison.InvariantCulture))
                return true;

            return allowed.host == "argon.gl" &&
                   uri.Host.EndsWith(".argon.gl", StringComparison.InvariantCulture);
        });
}
