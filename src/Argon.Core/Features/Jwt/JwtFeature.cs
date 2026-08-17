namespace Argon.Features.Jwt;

using Microsoft.Extensions.DependencyInjection;

public static class JwtFeature
{
    /// <summary>
    /// <see cref="JwtOptions"/> is bound by the feature that declares it, not here — one section, one
    /// owner, one validation rule.
    /// </summary>
    public static IServiceCollection AddJwt(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<WrapperForSignKey>();
        builder.Services.AddSingleton<WrapperForEncryptionKey>();
        builder.Services.AddScoped<ClassicJwtFlow>();
        builder.Services.AddSingleton<TokenAuthorization>();
        return builder.Services;
    }
}