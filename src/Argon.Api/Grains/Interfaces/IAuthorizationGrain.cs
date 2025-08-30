namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;
using Users;

[Alias("Argon.Grains.Interfaces.IAuthorizationGrain"), Unordered]
public interface IAuthorizationGrain : IGrainWithGuidKey
{
    [Alias("Authorize"), AlwaysInterleave]
    Task<Either<string, AuthorizationError>> Authorize(UserCredentialsInput input);

    [Alias("Register"), AlwaysInterleave]
    Task<Either<string, FailedRegistration>> Register(NewUserCredentialsInput input);

    //[Alias("BeginResetPass"), AlwaysInterleave]
    //Task<bool> BeginResetPass(string email);

    //[Alias("ResetPass"), AlwaysInterleave]
    //Task<Either<string, AuthorizationError>> ResetPass(UserResetPassInput resetData);
}