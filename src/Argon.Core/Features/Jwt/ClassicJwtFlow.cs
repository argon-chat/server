namespace Argon.Features.Jwt;

using Argon.Features.Auth;
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

    public string GenerateAccessToken(Guid userId, IEnumerable<string> scopes, IEnumerable<Claim>? additionalClaims = null)
    {
        var creds = new SigningCredentials(keyProvider.PrivateKey, keyProvider.Algorithm);
        var now   = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("type", "access")
        };
        claims.AddRange(additionalClaims ?? []);
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

    public string GenerateAccessToken(Guid userId, string machineId, IEnumerable<string> scopes, IEnumerable<Claim>? additionalClaims = null)
    {
        var creds = new SigningCredentials(keyProvider.PrivateKey, keyProvider.Algorithm);
        var now   = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("mh", HashMachineId(machineId)),
            new("type", "access")
        };
        claims.AddRange(additionalClaims ?? []);
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

    /// <summary>
    /// Mints a refresh token bound to a session id of the server's choosing.
    /// </summary>
    /// <remarks>
    /// <para>The <c>sid</c> is a signed claim rather than something read back off the request,
    /// which is the whole point of it being here. The session id the rest of the request pipeline
    /// uses comes out of the <c>ArgonSecure</c> cookie — a value the caller writes — so revocation
    /// keyed on that can be sidestepped by simply not sending it. A claim inside the signature
    /// cannot be, and it is what <see cref="ValidateRefreshTokenSession"/> hands the refresh path.</para>
    ///
    /// <para><c>iat</c> is written explicitly so a revocation floor can compare against it; see
    /// <c>SessionRevocation.FloorKey</c>.</para>
    /// </remarks>
    /// <param name="deviceThumbprint">
    /// Binds the token to a hardware key: the holder must be able to sign for it, not merely hold
    /// the token. Null leaves the token unbound, which is what every token minted before the device
    /// was enrolled looks like.
    /// </param>
    public string GenerateRefreshToken(
        Guid userId, string machineId, IEnumerable<string> scopes, Guid sessionId, string? deviceThumbprint = null)
    {
        var creds = new SigningCredentials(keyProvider.PrivateKey, keyProvider.Algorithm);

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("mh", HashMachineId(machineId)),
            new("sid", sessionId.ToString()),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("type", "refresh")
        };

        // Named after the RFC 8705 confirmation claim it plays the part of: proof-of-possession, so
        // a copied token is worthless without the key it names.
        if (!string.IsNullOrWhiteSpace(deviceThumbprint))
            claims.Add(new Claim("cnf", deviceThumbprint));
        claims.AddRange(scopes.Select(s => new Claim("scp", s)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow + SessionRevocation.RefreshTokenLifetime,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }


    public string GenerateRefreshToken(Guid userId, IEnumerable<string> scopes)
    {
        var creds = new SigningCredentials(keyProvider.PrivateKey, keyProvider.Algorithm);

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("type", "refresh")
        };
        claims.AddRange(scopes.Select(s => new Claim("scp", s)));

        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            DateTime.UtcNow,
            DateTime.UtcNow + SessionRevocation.RefreshTokenLifetime,
            creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateAccessToken(string token, string requiredScope)
        => ValidateToken(token, "", "access", requiredScope, out _, false);

    public (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateAccessToken(string token, string requiredScope,
        out List<Claim> claims)
        => ValidateToken(token, "", "access", requiredScope, out claims, validateMachineId: false);

    public (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateAccessToken(string token, string machineId, string requiredScope)
        => ValidateToken(token, machineId, "access", requiredScope, out _);

    /// <summary>
    /// Validates an access token and hands back the device it was minted for, when it names one.
    /// </summary>
    /// <remarks>
    /// The <c>did</c> claim is written only when the access token came from a refresh bound to a
    /// hardware key, so it is present exactly when the request path can be sure which machine is
    /// asking — which is what makes a hardware ban enforceable per request without a lookup.
    /// </remarks>
    public (Guid userId, Guid? deviceId) ValidateAccessTokenDevice(string token, string machineId, string requiredScope)
    {
        var (userId, _, _) = ValidateToken(token, machineId, "access", requiredScope, out var claims);

        var deviceId = claims.FirstOrDefault(c => c.Type == "did")?.Value is { } did && Guid.TryParse(did, out var parsed)
            ? parsed
            : (Guid?)null;

        return (userId, deviceId);
    }

    public (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateRefreshToken(string token, string machineId)
        => ValidateToken(token, machineId, "refresh", null, out _);

    /// <summary>
    /// Validates a refresh token and returns what revocation has to be checked against.
    /// </summary>
    /// <remarks>
    /// <paramref name="sessionId"/> is null for tokens minted before <c>sid</c> existed. Those
    /// cannot be revoked one at a time, which is what the issued-at floor is for — it is the only
    /// handle on a token that predates the claim, and every refresh token is dated ten years out.
    /// </remarks>
    public (Guid userId, IReadOnlyList<string> scopes) ValidateRefreshTokenSession(
        string token, string machineId, out Guid? sessionId, out DateTimeOffset? issuedAt, out string? deviceThumbprint)
    {
        var (userId, _, scopes) = ValidateToken(token, machineId, "refresh", null, out var claims);

        sessionId = claims.FirstOrDefault(c => c.Type == "sid")?.Value is { } sid && Guid.TryParse(sid, out var parsed)
            ? parsed
            : null;

        issuedAt = claims.FirstOrDefault(c => c.Type == "iat")?.Value is { } iat && long.TryParse(iat, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

        deviceThumbprint = claims.FirstOrDefault(c => c.Type == "cnf")?.Value;

        return (userId, scopes);
    }

    public bool TryValidateRefreshToken(string token, string machineId, out (Guid userId, string machineId, IReadOnlyList<string> scopes) data)
    {
        try
        {
            data = ValidateToken(token, machineId, "refresh", null, out _);
            return true;
        }
        catch(MachineIdNotMatchedException)
        {
            data = default;
            return false;
        }
        catch (BadUserIdException)
        {
            data = default;
            return false;
        }
        catch (TokenTypeNotAllowed)
        {
            data = default;
            return false;
        }
    }

    public bool TryValidateRefreshToken(string token, out (Guid userId, string machineId, IReadOnlyList<string> scopes) data)
    {
        try
        {
            data = ValidateToken(token, "", "refresh", null, out _, false);
            return true;
        }
        catch (MachineIdNotMatchedException)
        {
            data = default;
            return false;
        }
        catch (BadUserIdException)
        {
            data = default;
            return false;
        }
        catch (TokenTypeNotAllowed)
        {
            data = default;
            return false;
        }
    }

    private (Guid userId, string machineId, IReadOnlyList<string> scopes) ValidateToken(string token, string machineId, string expectedType,
        string? requiredScope, out List<Claim> claims, bool validateMachineId = true)
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


        claims = principal.Claims.ToList();

        return (uid, machineId, scopes);
    }
}

public class NotAllowedScopeException() : Exception();

public class BadUserIdException() : Exception();

public class MachineIdNotMatchedException() : Exception();

public class TokenTypeNotAllowed() : Exception();