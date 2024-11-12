namespace Argon.Api.Features.Jwt;

using Argon.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;

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

public enum TokenValidationError
{
    BAD_TOKEN,
    EXPIRED_TOKEN
}