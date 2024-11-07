namespace Argon.Api.Fusion;

using Argon.Contracts;
using Grains.Interfaces;

public class AuthorizationService(IGrainFactory grainFactory) : IUserAuthorization
{
    public Task<Either<JwtToken, AuthorizationError>> AuthorizeAsync(UserCredentialsInput request)
        => grainFactory.GetGrain<IAuthorizationGrain>(IAuthorizationGrain.DefaultId).Authorize(request);
}