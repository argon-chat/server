namespace Argon.Services.Ion;

using Core.Services.Validators;
using Features.Jwt;

public class IdentityInteraction(ILogger<IIdentityInteraction> logger, ClassicJwtFlow flow) : IIdentityInteraction
{
    public async Task<IAuthorizeResult> Authorize(UserCredentialsInput data, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).Authorize(data);

        if (result.IsSuccess)
            return new SuccessAuthorize(result.Value, null);
        return new FailedAuthorize(result.Error);
    }

    public async Task<IRegistrationResult> Registration(NewUserCredentialsInput data, CancellationToken ct = default)
    {
        var validationStatus = await new NewUserCredentialsInputValidator(this.GetUserCountry()).ValidateAsync(data, ct);

        if (!validationStatus.IsValid)
        {
            var err = validationStatus.Errors.First();
            return new FailedRegistration(RegistrationError.VALIDATION_FAILED, err.PropertyName, err.ErrorMessage);
        }

        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).Register(data);

        if (result.IsSuccess)
            return new SuccessRegistration(result.Value, null);
        return new FailedRegistration(result.Error.error, result.Error.field, result.Error.message);
    }

    public Task<bool> BeginResetPassword(string email, CancellationToken ct = default)
        => this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).BeginResetPass(email);

    public async Task<IAuthorizeResult> ResetPassword(string email, string otpCode, string newPassword, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).ResetPass(email, otpCode, newPassword);

        if (result.IsSuccess)
            return new SuccessAuthorize(result.Value, null);
        return new FailedAuthorize(result.Error);
    }

    public Task<string> GetAuthorizationScenario(CancellationToken ct = default)
        => Task.FromResult("Email_Otp");

    public Task<string> GetAuthorizationScenarioFor(UserLoginInput data, CancellationToken ct = default)
        => this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).GetAuthorizationScenarioFor(data, ct);

    public async Task<IMyAuthStatus> GetMyAuthorization(string token, string? refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
            return new BadAuthStatus(BadAuthKind.REQUIRED_RELOGIN);
        try
        {
            var machineId = this.GetMachineId();

            var (userId, _, scopes) = flow.ValidateRefreshToken(refreshToken, machineId);

            var limitation = await this.GetGrain<IUserGrain>(userId).GetLimitationForUser();

            if (limitation.lockdownReason is not null)
                return limitation;

            var newIssued = flow.GenerateAccessToken(userId, machineId, scopes);

            return new GoodAuthStatus(newIssued);
        }
        catch (InvalidOperationException e)
        {
            logger.LogWarning(e, "failed validate machineId");
            return new BadAuthStatus(BadAuthKind.REQUIRED_RELOGIN);
        }
        catch (TokenTypeNotAllowed e)
        {
            logger.LogWarning(e, "trying authorize by invalid scope token");
            return new BadAuthStatus(BadAuthKind.BAD_TOKEN);
        }
        catch (MachineIdNotMatchedException e)
        {
            logger.LogWarning(e, "trying authorize with not matched machineId");
            return new BadAuthStatus(BadAuthKind.SESSION_EXPIRED);
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed call GetMyAuthorization");
            return new BadAuthStatus(BadAuthKind.REQUIRED_RELOGIN);
        }
    }
}