﻿namespace Grains;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Orleans;
using Orleans.Runtime;
using States;

public class FusionGrain(
    [PersistentState("sessions", "OrleansStorage")] IPersistentState<FusionSession> sessionStorage,
    TokenValidationParameters JwtParameters) : Grain, IFusionSession
{
    public async ValueTask<bool> AuthorizeAsync(string token)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        tokenHandler.ValidateToken(token, JwtParameters, out var validatedToken);
        var jwt = (JwtSecurityToken)validatedToken;

        sessionStorage.State.Id           = Guid.Parse(jwt.Id);
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
    [Alias(nameof(AuthorizeAsync))]
    ValueTask<bool> AuthorizeAsync(string token);

    [Alias(nameof(GetState))]
    ValueTask<FusionSession> GetState();
}