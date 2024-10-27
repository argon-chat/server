namespace Argon.Api.Services;

using Contracts;
using Grains.Interfaces;

public class UserAuthorization(IGrainFactory grainFactory) : IUserAuthorization
{
    public async Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request)
    {
        // TODO machineKey
        var token = await grainFactory.GetGrain<IUserManager>(request.username).Authorize(request.password);
        return new AuthorizeResponse(token);
    }
}