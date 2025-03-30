namespace Argon.Features.Jwt;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Core;

public static class JwtFeature
{
    public static IServiceCollection AddJwt(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

        var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>();
        var tokenValidator = new TokenValidationParameters
        {
            ValidIssuer              = jwt.Issuer,
            ValidAudience            = jwt.Audience,
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            RequireSignedTokens      = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        };
        builder.Services.AddSingleton<TokenValidationParameters>(q =>
        {
            var jwtSection = q.GetRequiredService<IConfiguration>().GetSection("Jwt");
            return new TokenValidationParameters
            {
                ValidIssuer              = jwtSection.GetValue<string>("Issuer"),
                ValidAudience            = jwtSection.GetValue<string>("Audience"),
                ValidateIssuer           = true,
                ValidateAudience         = true,
                ValidateLifetime         = true,
                RequireSignedTokens      = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey =
                    new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(q.GetRequiredService<IConfiguration>().GetSection("Jwt").GetValue<string>("key")))
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
           .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, (options) => options.TokenValidationParameters = tokenValidator);

        return builder.Services;
    }
}