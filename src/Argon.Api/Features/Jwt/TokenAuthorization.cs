namespace Argon.Features.Jwt;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

public class TokenAuthorization(IServiceProvider provider, ILogger<TokenAuthorization> logger)
{
    public async ValueTask<Either<TokenUserData, TokenValidationError>> AuthorizeByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return TokenValidationError.BAD_TOKEN;

        await using var scope = provider.CreateAsyncScope();

        var tokenValidation = scope.ServiceProvider.GetRequiredKeyedService<TokenValidationParameters>("argon-validator");
        var tokenHandler    = new JwtSecurityTokenHandler();
        var tokenData       = tokenHandler.ReadJwtToken(token);

        if (string.IsNullOrEmpty(tokenData.Header.Kid))
            return TokenValidationError.BAD_TOKEN;

        try
        {
            var principal = tokenHandler.ValidateToken(token, tokenValidation, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwtToken ||
                !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha512, StringComparison.InvariantCultureIgnoreCase))
                return TokenValidationError.BAD_TOKEN;
            var idClaim        = principal.FindFirst("id");
            var machineIdClaim = principal.FindFirst("mid");

            if (idClaim != null && machineIdClaim != null &&
                Guid.TryParse(idClaim.Value, out var id))
                return new TokenUserData(id, machineIdClaim.Value);

            return TokenValidationError.BAD_TOKEN;
        }
        catch (SecurityTokenExpiredException)
        {
            return TokenValidationError.EXPIRED_TOKEN;
        }
        catch (Exception e)
        {
            
            var existKid  = tokenValidation.IssuerSigningKeyResolver("", null, "", null).First().KeyId;
            logger.LogCritical(e, "Failed validate token, kid from key {kid}, kid in system: {existKid}", tokenData.Header.Kid, existKid);
            return TokenValidationError.BAD_TOKEN;
        }
    }
}