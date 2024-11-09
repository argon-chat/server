//namespace Argon.Api.Services;

//using Contracts;
//using Grains.Interfaces;

//public class UserAuthorization(IGrainFactory grainFactory) : IUserAuthorization
//{
//    public async Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request)
//    {
//        // TODO machineKey
//        var token = await grainFactory.GetGrain<IUserAuthorizationManager>(Guid.NewGuid())
//            .Authorize(new UserCredentialsInput(Username: request.username, Password: request.password,
//                Email: request.email, PhoneNumber: request.phoneNumber, GenerateOtp: request.generateOtp,
//                PasswordConfirmation: request.password));
//        return new AuthorizeResponse(token.token);
//    }
//}

