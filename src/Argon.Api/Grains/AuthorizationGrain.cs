namespace Argon.Grains;

using Features.Auth;
using Orleans.Concurrency;

[StatelessWorker]
public class AuthorizationGrain(
    IArgonAuthorizationService authorizationService
) : Grain, IAuthorizationGrain
{
    public async Task<Either<SuccessAuthorize, AuthorizationError>> Authorize(UserCredentialsInput input)
        => await authorizationService.Authorize(input, this.GetUserIp() ?? "unknown", this.GetUserMachineId());

    public async Task<Either<SuccessAuthorize, AuthorizationError>> ExternalAuthorize(UserCredentialsInput input)
        => await authorizationService.ExternalAuthorize(input);

    public async Task<Either<SuccessAuthorize, FailedRegistration>> Register(NewUserCredentialsInput input)
        => await authorizationService.Register(input, this.GetUserMachineId());

    public async Task<bool> BeginResetPass(string email)
        => await authorizationService.BeginResetPass(email, this.GetUserIp() ?? "unknown", this.GetUserMachineId());

    public async Task<Either<SuccessAuthorize, AuthorizationError>> ResetPass(string email, string otpCode, string newPassword)
        => await authorizationService.ResetPass(email, otpCode, newPassword, this.GetUserMachineId());

    public async Task<string> GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct)
        => await authorizationService.GetAuthorizationScenarioFor(data, ct);
}