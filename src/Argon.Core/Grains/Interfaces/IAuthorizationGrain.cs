namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

[Alias("Argon.Grains.Interfaces.IAuthorizationGrain"), Unordered]
public interface IAuthorizationGrain : IGrainWithGuidKey
{
    [Alias("Authorize"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, AuthorizationError>> Authorize(UserCredentialsInput input);

    [Alias("ExternalAuthorize"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, AuthorizationError>> ExternalAuthorize(UserCredentialsInput input);

    [Alias("Register"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, FailedRegistration>> Register(NewUserCredentialsInput input);

    [Alias("BeginResetPass"), AlwaysInterleave]
    Task<bool> BeginResetPass(string email);

    [Alias("ResetPass"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword);

    [Alias(nameof(GetAuthorizationScenarioFor))]
    Task<string> GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct);


}