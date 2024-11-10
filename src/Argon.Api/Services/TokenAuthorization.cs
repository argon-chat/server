namespace Argon.Api.Services;

using System.IdentityModel.Tokens.Jwt;
using Argon.Contracts;
using Microsoft.IdentityModel.Tokens;

public class TokenAuthorization(TokenValidationParameters tokenValidation)
{
    public async ValueTask<Either<TokenUserData, TokenValidationError>> AuthorizeByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return TokenValidationError.BAD_TOKEN;
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        try
        {
            var principal = tokenHandler.ValidateToken(token, tokenValidation, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwtToken ||
                !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha512, StringComparison.InvariantCultureIgnoreCase))
                return TokenValidationError.BAD_TOKEN;
            var idClaim        = principal.FindFirst("id");
            var machineIdClaim = principal.FindFirst("machineId");

            if (idClaim != null && machineIdClaim != null &&
                Guid.TryParse(idClaim.Value, out var id) &&
                Guid.TryParse(machineIdClaim.Value, out var machineId))
                return new TokenUserData(id, machineId);

            return TokenValidationError.BAD_TOKEN;
        }
        catch (SecurityTokenExpiredException)
        {
            return TokenValidationError.EXPIRED_TOKEN;
        }
        catch (Exception)
        {
            return TokenValidationError.BAD_TOKEN;
        }
    }
}

public enum TokenValidationError
{
    BAD_TOKEN,
    EXPIRED_TOKEN
}
public record TokenUserData(Guid id, Guid machineId);