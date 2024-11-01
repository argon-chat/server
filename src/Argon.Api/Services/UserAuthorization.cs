namespace Argon.Api.Services;

using Contracts;
using Grains.Interfaces;

public class UserAuthorization(IGrainFactory grainFactory) : IUserAuthorization
{
    public async Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request)
    {
        // TODO machineKey
        var token = await grainFactory.GetGrain<IUserAuthorizationManager>(Guid.NewGuid())
            .Authorize(new UserCredentialsInput(Username: request.username, Password: request.password,
                Email: null, PhoneNumber: null,
                PasswordConfirmation: request.password));
        return new AuthorizeResponse(token.token);
    }
}