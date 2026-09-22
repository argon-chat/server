namespace Argon.Features.Auth;

using Argon.Features.Integrations.Phones;
using Services;

public static class AuthorizationFeature
{
    /// <param name="withDatabase">
    /// Whether this role opens a connection to the application database.
    /// <para>Two of the services below take an <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>,
    /// and the identity server deliberately has none — it reads everything about applications and
    /// people through grains, because it is the role exposed to the whole internet and the further
    /// it sits from the data the less a mistake on it costs. Registered unconditionally they were a
    /// promise the container could not keep: nothing resolved them there, so nothing failed, until
    /// the first run with <c>ASPNETCORE_ENVIRONMENT=Development</c> — where the container validates
    /// every descriptor at build time and the whole role refused to start.</para>
    /// <para>Which is why this is a parameter rather than a probe of the service collection: whether
    /// a role has a database is a property of the role, known before anything is registered, and not
    /// something to infer from what happens to have been added first.</para>
    /// </param>
    public static void AddArgonAuthorization(this WebApplicationBuilder builder, bool withDatabase = true)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
        builder.Services.AddSingleton<UserManagerService>();
        builder.Services.AddSingleton<IQrLoginService, QrLoginService>();
        builder.Services.AddDataProtection();
        if (withDatabase)
            builder.Services.AddScoped<IArgonAuthorizationService, ArgonAuthorizationService>();

        // Device identity. The fingerprint half is a heuristic and the key half is a proof; both are
        // registered because they answer for different clients — see DeviceFingerprint for what the
        // vector can and cannot settle.
        // Singleton: it holds the frozen weight table, and rebuilding that per request would be the
        // most expensive part of reading a cookie.
        builder.Services.AddSingleton<DeviceMatcher>();

        builder.Services.AddScoped<DeviceProofVerifier>();

        // One verifier per platform. Anything not registered here falls back to the unattested
        // verifier, which grants KEY for a bare enrolment and refuses a blob it cannot check.
        // Singleton so the fetched roots are shared and fetched once, not per request.
        builder.Services.AddHttpClient(nameof(AndroidAttestationRoots));
        builder.Services.AddSingleton<AndroidAttestationRoots>();
        builder.Services.AddScoped<IDeviceAttestationVerifier, AndroidKeyAttestationVerifier>();
        builder.AddPhoneVerification();
    }
}

public class ArgonAuthOptions
{
    public AuthorizationScenario Scenario { get; set; } = AuthorizationScenario.Email_Pwd_Otp;
}

public enum AuthorizationScenario
{
    Email_Pwd_Otp,
    Email_Otp,
    Phone_Otp,
    SSO
}