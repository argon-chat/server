namespace Argon.Features.Auth;

using Services;

public static class AuthorizationFeature
{
    public static void AddArgonAuthorization(this WebApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
        builder.Services.AddTransient<UserManagerService>();
        builder.Services.AddDataProtection();
    }
}