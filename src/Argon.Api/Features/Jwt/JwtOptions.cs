namespace Argon.Api.Features.Jwt;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

public record JwtOptions
{
    public required string Issuer { get; set; }

    public required string Audience { get; set; }

    // TODO use cert in production
    public required string   Key     { get; set; }
    public required TimeSpan Expires { get; set; }

    public void Deconstruct(out string issuer, out string audience, out string key, out TimeSpan expires)
    {
        audience = Audience;
        issuer   = Issuer;
        key      = Key;
        expires  = Expires;
    }
}

public static class JwtFeature
{
    public static IServiceCollection AddJwt(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

        var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>();

        builder.Services.AddSingleton(new TokenValidationParameters
        {
            ValidIssuer              = jwt.Issuer,
            ValidAudience            = jwt.Audience,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ClockSkew                = TimeSpan.Zero
        });
        return builder.Services;
    }
}