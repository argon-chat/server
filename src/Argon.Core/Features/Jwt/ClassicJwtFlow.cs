namespace Argon.Features.Jwt;

using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

public sealed class ClassicJwtFlow(IOptions<JwtOptions> options, WrapperForSignKey keyProvider)
{
    private readonly JwtOptions _options = options.Value;
    private readonly byte[] _machineSalt = Encoding.UTF8.GetBytes(options.Value.MachineSalt
                                                                  ?? throw new InvalidOperationException(
                                                                      "Missing Jwt:MachineSalt in configuration"));

    private string HashMachineId(string machineId)
    {
        using var hmac = new HMACSHA256(_machineSalt);
        var       hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(machineId));
        return Convert.ToBase64String(hash);
    }

    private bool CompareMachineHash(string machineId, string? mhToken)
    {
        if (mhToken == null) return false;
        var computed = HashMachineId(machineId);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(computed),
            Convert.FromBase64String(mhToken));
    }

    public string GenerateAccessToken(Guid userId, IEnumerable<string> scopes)
    {
        var creds = new SigningCredentials(keyProvider.PrivateKey, keyProvider.Algorithm);
        var now   = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("type", "access")
        };
        claims.AddRange(scopes.Select(s => new Claim("scp", s)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now + _options.AccessTokenLifetime,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateAccessToken(Guid userId, string machineId, IEnumerable<string> scopes)
    {
        var creds = new SigningCredentials(keyProvider.PrivateKey, keyProvider.Algorithm);
        var now   = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("mh", HashMachineId(machineId)),
            new("type", "access")
        };
        claims.AddRange(scopes.Select(s => new Claim("scp", s)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now + _options.AccessTokenLifetime,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken(Guid userId, string machineId, IEnumerable<string> scopes)
    {
        var creds = new SigningCredentials(keyProvider.PrivateKey, keyProvider.Algorithm);

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("mh", HashMachineId(machineId)),
            new("type", "refresh")
        };
        claims.AddRange(scopes.Select(s => new Claim("scp", s)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddYears(10),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }


    public (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateAccessToken(string token, string requiredScope)
        => ValidateToken(token, "", "access", requiredScope, validateMachineId: false);

    public (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateAccessToken(string token, string machineId, string requiredScope)
        => ValidateToken(token, machineId, "access", requiredScope);

    public (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateRefreshToken(string token, string machineId)
        => ValidateToken(token, machineId, "refresh", null);

    private (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateToken(string token, string machineId, string expectedType,
        string? requiredScope, bool validateMachineId = true)
    {
        var handler = new JwtSecurityTokenHandler();

        handler.InboundClaimTypeMap.Clear();
        handler.OutboundClaimTypeMap.Clear();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidIssuer              = _options.Issuer,
            ValidAudience            = _options.Audience,
            ValidateLifetime         = true,
            RequireSignedTokens      = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = keyProvider.PublicKey,
            ClockSkew                = TimeSpan.FromMinutes(2)
        };


        var principal = handler.ValidateToken(token, parameters, out _);

        var type = principal.FindFirst("type")?.Value;
        if (type != expectedType)
            throw new TokenTypeNotAllowed();

        if (validateMachineId)
        {
            var mh = principal.FindFirst("mh")?.Value;
            if (!CompareMachineHash(machineId, mh))
                throw new MachineIdNotMatchedException();
        }
        
        var scopes = principal.FindAll("scp").Select(c => c.Value).ToArray();

        if (requiredScope != null && !scopes.Contains(requiredScope))
            throw new NotAllowedScopeException();

        var sub = principal.FindFirstValue("sub") ??
                  principal.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
        if (!Guid.TryParse(sub, out var uid))
            throw new BadUserIdException();

        return (uid, machineId, scopes);
    }
}

public class NotAllowedScopeException() : Exception();

public class BadUserIdException() : Exception();

public class MachineIdNotMatchedException() : Exception();

public class TokenTypeNotAllowed() : Exception();