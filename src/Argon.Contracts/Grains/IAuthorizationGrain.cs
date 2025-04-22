namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;
using Users;

[Alias("Argon.Grains.Interfaces.IAuthorizationGrain"), Unordered]
public interface IAuthorizationGrain : IGrainWithGuidKey
{
    [Alias("Authorize"), AlwaysInterleave]
    Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input, UserConnectionInfo connectionInfo);

    [Alias("Register"), AlwaysInterleave]
    Task<Either<string, RegistrationError>> Register(NewUserCredentialsInput input, UserConnectionInfo connectionInfo);
}