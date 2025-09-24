namespace Argon.Services.Ion;

using Argon.Services.Validators;
using Features.Auth;

public class IdentityInteraction(IOptions<ArgonAuthOptions> authOptions) : IIdentityInteraction
{
    public async Task<IAuthorizeResult> Authorize(UserCredentialsInput data)
    {
        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).Authorize(data);

        if (result.IsSuccess)
            return new SuccessAuthorize(result.Value, null);
        return new FailedAuthorize(result.Error);
    }

    public async Task<IRegistrationResult> Registration(NewUserCredentialsInput data)
    {
        var validationStatus = await new NewUserCredentialsInputValidator(this.GetUserCountry()).ValidateAsync(data);

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

    public Task<bool> BeginResetPassword(string email)
        => this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).BeginResetPass(email);

    public async Task<IAuthorizeResult> ResetPassword(string email, string otpCode, string newPassword)
    {
        var result = await this.GetGrain<IAuthorizationGrain>(Guid.NewGuid()).ResetPass(email, otpCode, newPassword);

        if (result.IsSuccess)
            return new SuccessAuthorize(result.Value, null);
        return new FailedAuthorize(result.Error);
    }

    public Task<string> GetAuthorizationScenario()
        => Task.FromResult(authOptions.Value.Scenario.ToString());
}