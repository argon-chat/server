namespace Argon.Features.Auth;

using Argon.Features.Integrations.Phones;
using Services;

public static class AuthorizationFeature
{
    public static void AddArgonAuthorization(this WebApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
        builder.Services.AddSingleton<UserManagerService>();
        builder.Services.AddSingleton<IQrLoginService, QrLoginService>();
        builder.Services.AddDataProtection();
        builder.Services.AddScoped<IArgonAuthorizationService, ArgonAuthorizationService>();

        // Device identity. The fingerprint half is a heuristic and the key half is a proof; both are
        // registered because they answer for different clients — see DeviceFingerprint for what the
        // vector can and cannot settle.
        // Singleton: it holds the frozen weight table, and rebuilding that per request would be the
        // most expensive part of reading a cookie.
        builder.Services.AddSingleton<DeviceMatcher>();
        builder.Services.AddScoped<DeviceIdentityService>();
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