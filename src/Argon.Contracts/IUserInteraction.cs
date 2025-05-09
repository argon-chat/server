namespace Argon;

using Shared.Servers;
using Users;

[AttributeUsage(AttributeTargets.Method)]
public class AllowAnonymousAttribute : Attribute;

[TsInterface]
public interface IUserInteraction : IArgonService
{
    Task<User>            GetMe();
    Task<ServerDto>       CreateServer(CreateServerRequest request);
    Task<List<ServerDto>> GetServers();


    [AllowAnonymous]
    Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input);

    [AllowAnonymous]
    Task<Either<string, RegistrationError>> Registration(NewUserCredentialsInput input);

    [AllowAnonymous]
    Task<bool> BeginResetPassword(string email);

    [AllowAnonymous]
    Task<Either<string, AuthorizationError>> ResetPassword(UserResetPassInput input);


    Task<Either<Server, AcceptInviteError>> JoinToServerAsync(InviteCode inviteCode);


    Task BroadcastPresenceAsync(UserActivityPresence presence);
    Task RemoveBroadcastPresenceAsync();

    Task<bool>   CompleteSocialBoundAsync(string token, string socialUser, string kind, string userSlash);
    Task<string> CreateSocialBoundAsync(string kind);
}