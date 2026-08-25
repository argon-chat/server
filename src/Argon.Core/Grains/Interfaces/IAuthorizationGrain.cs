namespace Argon.Grains.Interfaces;

using Argon.Features.Auth;
using Orleans.Concurrency;

[Alias("Argon.Grains.Interfaces.IAuthorizationGrain")]
public interface IAuthorizationGrain : IGrainWithGuidKey
{
    [Alias("Authorize"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, AuthorizationError>> Authorize(UserCredentialsInput input);

    [Alias("ExternalAuthorize"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, AuthorizationError>> ExternalAuthorize(UserCredentialsInput input);

    [Alias("Register"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, FailedRegistration>> Register(NewUserCredentialsInput input);

    [Alias(nameof(ExternalRegister)), AlwaysInterleave]
    Task<Either<SuccessAuthorize, FailedRegistration>> ExternalRegister(NewUserCredentialsInput input);

    [Alias("BeginResetPass"), AlwaysInterleave]
    Task<bool> BeginResetPass(string email);

    [Alias("ResetPass"), AlwaysInterleave]
    Task<Either<SuccessAuthorize, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword);

    [Alias(nameof(GetAuthorizationScenarioFor))]
    Task<string> GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct);

    // Passkey sign-in, in the three steps the ceremony takes: hand out the challenge, verify the
    // assertion, and — for an account that also has one-time codes on — confirm the code that the
    // second step asked for. Each step's state lives behind the nonce it returns, not in the caller,
    // so an identity server that never opens a database of its own can still drive the whole flow.

    [Alias(nameof(BeginPasskeyLogin)), AlwaysInterleave]
    Task<BeginPasskeyLoginResult> BeginPasskeyLogin(string? email, CancellationToken ct);

    [Alias(nameof(CompletePasskeyLogin)), AlwaysInterleave]
    Task<PasskeyLoginResult> CompletePasskeyLogin(string assertionResponseJson, CancellationToken ct);

    [Alias(nameof(ConfirmPasskeyOtp)), AlwaysInterleave]
    Task<PasskeyLoginResult> ConfirmPasskeyOtp(string passkeyNonce, string otpCode, CancellationToken ct);


}