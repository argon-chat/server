namespace Argon.Api.Grains;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Persistence.States;

public class FusionGrain(
    [PersistentState("sessions", "OrleansStorage")]
    IPersistentState<FusionSession> sessionStorage,
    TokenValidationParameters JwtParameters) : Grain, IFusionSession
{
    public async ValueTask<bool> AuthorizeAsync(string token)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        tokenHandler.ValidateToken(token, JwtParameters, out SecurityToken validatedToken);
        var jwt = (JwtSecurityToken)validatedToken;

        sessionStorage.State.Id = Guid.Parse(jwt.Id);
        sessionStorage.State.IsAuthorized = true;
        await sessionStorage.WriteStateAsync();
        return true;
    }

    public async ValueTask<FusionSession> GetState()
    {
        await sessionStorage.ReadStateAsync();
        return sessionStorage.State;
    }

}


public interface IFusionSession : IGrainWithGuidKey
{
    [Alias("AuthorizeAsync")]
    ValueTask<bool> AuthorizeAsync(string token);

    [Alias("GetState")]
    ValueTask<FusionSession> GetState();
}