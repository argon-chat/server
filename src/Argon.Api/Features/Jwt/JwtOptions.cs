namespace Argon.Api.Features.Jwt;

using System.Text;
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
        builder.Services.Configure<JwtOptions>(config: builder.Configuration.GetSection(key: "Jwt"));

        var jwt = builder.Configuration.GetSection(key: "Jwt").Get<JwtOptions>();

        builder.Services.AddAuthentication(configureOptions: options =>
                                                             {
                                                                 options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                                                                 options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
                                                                 options.DefaultScheme             = JwtBearerDefaults.AuthenticationScheme;
                                                             }).AddJwtBearer(configureOptions: o =>
                                                                                               {
                                                                                                   o.TokenValidationParameters =
                                                                                                       new TokenValidationParameters
                                                                                                       {
                                                                                                           ValidIssuer   = jwt.Issuer,
                                                                                                           ValidAudience = jwt.Audience,
                                                                                                           IssuerSigningKey =
                                                                                                               new SymmetricSecurityKey(key: Encoding
                                                                                                                   .UTF8.GetBytes(s: jwt.Key)),
                                                                                                           ValidateIssuer           = true,
                                                                                                           ValidateAudience         = true,
                                                                                                           ValidateLifetime         = true,
                                                                                                           ValidateIssuerSigningKey = true,
                                                                                                           ClockSkew                = TimeSpan.Zero
                                                                                                       };
                                                                                                   o.Events = new JwtBearerEvents
                                                                                                   {
                                                                                                       OnMessageReceived = ctx =>
                                                                                                       {
                                                                                                           if (ctx.Request.Headers
                                                                                                            .TryGetValue(key: "x-argon-token",
                                                                                                             value: out var value))
                                                                                                           {
                                                                                                               ctx.Token = value;
                                                                                                               return Task.CompletedTask;
                                                                                                           }

                                                                                                           ctx.Response.StatusCode = 401;
                                                                                                           return Task.CompletedTask;
                                                                                                       }
                                                                                                   };
                                                                                               });
        return builder.Services;
    }
}