namespace Argon.Features.Auth;

public interface IArgonAuthorizationService
{
    Task<Either<SuccessAuthorize, AuthorizationError>> Authorize(UserCredentialsInput input, string userIp, string machineId);
    Task<Either<SuccessAuthorize, AuthorizationError>> ExternalAuthorize(UserCredentialsInput input);
    Task<Either<SuccessAuthorize, FailedRegistration>> Register(NewUserCredentialsInput input, string machineId);
    Task<bool>                                         BeginResetPass(string email, string userIp, string machineId);
    Task<Either<SuccessAuthorize, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword, string userMachineId);
    Task<string>                 GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct);
}