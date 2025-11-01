namespace Argon.Features.Jwt;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

public class TokenAuthorization(IServiceProvider provider, ILogger<TokenAuthorization> logger)
{
    public async ValueTask<Either<TokenUserData, TokenValidationError>> AuthorizeByToken(string token, string machineId)
    {
        if (string.IsNullOrEmpty(token))
            return TokenValidationError.BAD_TOKEN;

        await using var scope = provider.CreateAsyncScope();

        var tokenValidation = scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>();
        var tokenHandler    = new JwtSecurityTokenHandler();
        var tokenData       = tokenHandler.ReadJwtToken(token);

        //if (string.IsNullOrEmpty(tokenData.Header.Kid))
        //    return TokenValidationError.BAD_TOKEN;

        try
        {
            var (userId, _, scopes) = tokenValidation.ValidateAccessToken(token, machineId, "argon.app");

            return new TokenUserData(userId, machineId);
        }
        catch (SecurityTokenExpiredException)
        {
            return TokenValidationError.EXPIRED_TOKEN;
        }
        catch (NotAllowedScopeException)
        {
            return TokenValidationError.BAD_TOKEN;
        }
        catch (BadUserIdException)
        {
            return TokenValidationError.BAD_TOKEN;
        }
        catch (MachineIdNotMatchedException)
        {
            return TokenValidationError.BAD_TOKEN;
        }
        catch (TokenTypeNotAllowed)
        {
            return TokenValidationError.BAD_TOKEN;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed validate token, kid from key {kid}", tokenData.Header.Kid);
            return TokenValidationError.BAD_TOKEN;
        }
    }
}

/*public class NotAllowedScopeException() : Exception();
public class BadUserIdException() : Exception();
public class MachineIdNotMatchedException() : Exception();
public class TokenTypeNotAllowed() : Exception();*/