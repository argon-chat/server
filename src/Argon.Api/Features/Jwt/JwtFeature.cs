namespace Argon.Features.Jwt;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

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
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateIssuer           = false,
            ValidateAudience         = false,
            ValidateLifetime         = false,
            ValidateIssuerSigningKey = false,
            ClockSkew                = TimeSpan.Zero
        };

        builder.Services.AddSingleton(tokenValidator);
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