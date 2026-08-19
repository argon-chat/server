namespace ArgonComplexTest.Infrastructure;

using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>
/// A browser talking to the identity server.
/// </summary>
/// <remarks>
/// Over <c>https</c>, and that is not decoration. The session cookie is <c>Secure</c> and
/// <c>SameSite=None</c> — it has to be, because the sign-in widget is framed by sites that are not
/// ours — and a cookie container will accept such a cookie over plain HTTP and then never send it
/// back. Every test of a signed-in flow would fail as "no session" with nothing to point at.
/// <para>
/// Redirects are not followed: the OAuth endpoints answer with them, and where they point is usually
/// the thing under test.
/// </para>
/// </remarks>
public static class AegisClient
{
    public static HttpClient For(RoleHost host)
        => host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress       = new Uri("https://aegis.test.local"),
            HandleCookies     = true,
            AllowAutoRedirect = false
        });
}
