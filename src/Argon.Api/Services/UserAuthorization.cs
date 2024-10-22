namespace Argon.Api.Services;

using Grains.Interfaces;
using Contracts;

public class UserAuthorization(IGrainFactory grainFactory) : IUserAuthorization
{
    public async Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request)
    {
        // TODO machineKey
        var token = await grainFactory.GetGrain<IUserManager>(request.username).Authenticate(request.password);
        return new AuthorizeResponse(token, [new ServerResponse(Guid.NewGuid(), "xuita", null)]);
    }
}