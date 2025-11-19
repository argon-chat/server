namespace Argon.Features.Jwt;

using Microsoft.Extensions.DependencyInjection;

public static class JwtFeature
{
    public static IServiceCollection AddJwt(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

        builder.Services.AddScoped<WrapperForSignKey>();
        builder.Services.AddScoped<WrapperForEncryptionKey>();
        builder.Services.AddScoped<ClassicJwtFlow>();
        builder.Services.AddSingleton<TokenAuthorization>();
        return builder.Services;
    }
}