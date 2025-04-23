namespace Argon.Features.Jwt;

using System.IdentityModel.Tokens.Jwt;
using System.IO.Hashing;
using k8s.KubeConfigModels;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Text;

public class PostConfigurator(IssuerSigningKeyResolver resolverKeysStore, IOptions<JwtOptions> jwtOptions) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;
        options.TokenValidationParameters.IssuerSigningKeyResolver = resolverKeysStore;
        options.TokenValidationParameters.ValidAudience            = jwtOptions.Value.Audience;
        options.TokenValidationParameters.ValidIssuer              = jwtOptions.Value.Issuer;

        options.TokenValidationParameters.ValidateIssuer           = true;
        options.TokenValidationParameters.ValidateAudience         = true;
        options.TokenValidationParameters.ValidateLifetime         = true;
        options.TokenValidationParameters.RequireSignedTokens      = true;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
    }
}

public static class JwtFeature
{
    public static IServiceCollection AddJwt(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        builder.Services.AddScoped<IssuerSigningKeyResolver>(q => (_, _, _, _) =>
        {
            var opt    = q.GetRequiredService<IOptions<JwtOptions>>();
            var logger = q.GetRequiredService<ILogger<IssuerSigningKeyResolver>>();
            logger.LogCritical("LOADED '{key}' ", opt.Value.Key);

            var keyBytes = Encoding.UTF8.GetBytes(opt.Value.Key);
            return
            [
                new SymmetricSecurityKey(keyBytes)
                {
                    KeyId = $"{Crc64.HashToUInt64(keyBytes):X}"
                }
            ];
        });

        builder.Services.AddScoped<TokenValidationParameters>(q =>
        {
            var jwtSection       = q.GetRequiredService<IOptions<JwtOptions>>();
            var resolverKeyStore = q.GetRequiredService<IssuerSigningKeyResolver>();
            return new TokenValidationParameters
            {
                ValidIssuer              = jwtSection.Value.Issuer,
                ValidAudience            = jwtSection.Value.Audience,
                ValidateIssuer           = true,
                ValidateAudience         = true,
                ValidateLifetime         = true,
                RequireSignedTokens      = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeyResolver = resolverKeyStore
            };
        });
        builder.Services.AddSingleton<TokenAuthorization>();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(JwtBearerDefaults.AuthenticationScheme, policy =>
            {
                policy.AuthenticationSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
        });
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
            })
           .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme);

        return builder.Services;
    }
}