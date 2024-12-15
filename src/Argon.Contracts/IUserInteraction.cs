namespace Argon;

using Users;

[AttributeUsage(AttributeTargets.Method)]
public class AllowAnonymousAttribute : Attribute;

[TsInterface]
public interface IUserInteraction : IArgonService
{
    Task<User>         GetMe();
    Task<Server>       CreateServer(CreateServerRequest request);
    Task<List<Server>> GetServers();


    [AllowAnonymous]
    Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input);

    [AllowAnonymous]
    Task<Either<string, RegistrationError>> Registration(NewUserCredentialsInput input);
}