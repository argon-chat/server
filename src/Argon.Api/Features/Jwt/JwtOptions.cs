namespace Argon.Api.Features.Jwt;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

public record JwtOptions
{
    public required string Issuer { get; set; }
    public required string Audience { get; set; }
    // TODO use cert in production
    public required string Key { get; set; }
    public required TimeSpan Expires { get; set; }

    public void Deconstruct(out string issuer, out string audience, out string key)
    {
        audience = this.Audience;
        issuer = this.Issuer;
        key = this.Key;
    }
}


public static class JwtFeature
{
    public static IServiceCollection AddJwt(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        builder.Services.AddKeyedSingleton(JwtBearerDefaults.AuthenticationScheme,
            (services, _) =>
            {
                var options = services.GetRequiredService<IOptions<JwtOptions>>();
                var (issuer, audience, key) = options.Value;
                return new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ClockSkew = TimeSpan.Zero
                };
            });

        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(o =>
        {
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (ctx.Request.Headers.TryGetValue("x-argon-token", out var value))
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