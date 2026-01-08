namespace Argon.Features.Auth;

using Argon.Features.Integrations.Phones;
using Services;

public static class AuthorizationFeature
{
    public static void AddArgonAuthorization(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<ArgonAuthOptions>(builder.Configuration.GetSection("auth"));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
        builder.Services.AddSingleton<UserManagerService>();
        builder.Services.AddDataProtection();
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