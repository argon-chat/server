namespace Argon.Features.Auth;

public interface IArgonAuthorizationService
{
    Task<Either<SuccessAuthorize, AuthorizationError>> Authorize(UserCredentialsInput input, string userIp, string machineId);
    Task<Either<SuccessAuthorize, AuthorizationError>> ExternalAuthorize(UserCredentialsInput input);
    Task<Either<SuccessAuthorize, FailedRegistration>> Register(NewUserCredentialsInput input, string machineId);
    Task<Either<SuccessAuthorize, FailedRegistration>> ExternalRegister(NewUserCredentialsInput input);
    Task<bool>                                         BeginResetPass(string email, string userIp, string machineId);
    Task<Either<SuccessAuthorize, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword, string userMachineId);
    Task<string>                 GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct);

    Task<BeginPasskeyLoginResult> BeginPasskeyLogin(string? email, CancellationToken ct);
    Task<PasskeyLoginResult>      CompletePasskeyLogin(string assertionResponseJson, CancellationToken ct);
    Task<PasskeyLoginResult>      ConfirmPasskeyOtp(string passkeyNonce, string otpCode, CancellationToken ct);
}

public record BeginPasskeyLoginResult(string? OptionsJson, PasskeyLoginError Error);

public record PasskeyLoginResult(
    bool Success,
    string? Token,
    Guid? UserId,
    bool RequiresOtp,
    string? PasskeyNonce,
    PasskeyLoginError Error);

public enum PasskeyLoginError
{
    NONE,
    NO_PASSKEYS,
    USER_NOT_FOUND,
    INVALID_ASSERTION,
    VERIFICATION_FAILED,
    CHALLENGE_EXPIRED,
    BAD_OTP,
    NONCE_EXPIRED,
    INTERNAL_ERROR
}