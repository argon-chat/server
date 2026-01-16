namespace Argon.Services.Ion;

using ion.runtime;

public class SecurityInteractionImpl : ISecurityInteraction
{
    private ISecurityGrain SecurityGrain => this.GetGrain<ISecurityGrain>(this.GetUserId());

    public async Task<IRequestEmailChangeResult> RequestEmailChange(string newEmail, string password, CancellationToken ct = default)
        => await SecurityGrain.RequestEmailChangeAsync(newEmail, password, ct);

    public async Task<IConfirmEmailChangeResult> ConfirmEmailChange(string verificationCode, CancellationToken ct = default)
        => await SecurityGrain.ConfirmEmailChangeAsync(verificationCode, ct);

    public async Task<IRequestPhoneChangeResult> RequestPhoneChange(string newPhone, string password, CancellationToken ct = default)
        => await SecurityGrain.RequestPhoneChangeAsync(newPhone, password, ct);

    public async Task<IConfirmPhoneChangeResult> ConfirmPhoneChange(string verificationCode, CancellationToken ct = default)
        => await SecurityGrain.ConfirmPhoneChangeAsync(verificationCode, ct);

    public async Task<IRemovePhoneResult> RemovePhone(string password, CancellationToken ct = default)
        => await SecurityGrain.RemovePhoneAsync(password, ct);

    public async Task<IChangePasswordResult> ChangePassword(string currentPassword, string newPassword, CancellationToken ct = default)
        => await SecurityGrain.ChangePasswordAsync(currentPassword, newPassword, ct);

    public async Task<IEnableOTPResult> EnableOTP(CancellationToken ct = default)
        => await SecurityGrain.EnableOTPAsync(ct);

    public async Task<IVerifyOTPResult> VerifyAndEnableOTP(string code, CancellationToken ct = default)
        => await SecurityGrain.VerifyAndEnableOTPAsync(code, ct);

    public async Task<IDisableOTPResult> DisableOTP(string code, CancellationToken ct = default)
        => await SecurityGrain.DisableOTPAsync(code, ct);

    public async Task<IonArray<Passkey>> GetPasskeys(CancellationToken ct = default)
        => new(await SecurityGrain.GetPasskeysAsync(ct));

    public async Task<IBeginPasskeyResult> BeginAddPasskey(string name, CancellationToken ct = default)
        => await SecurityGrain.BeginAddPasskeyAsync(name, ct);

    public async Task<ICompletePasskeyResult> CompleteAddPasskey(Guid passkeyId, string publicKey, CancellationToken ct = default)
        => await SecurityGrain.CompleteAddPasskeyAsync(passkeyId, publicKey, ct);

    public async Task<IRemovePasskeyResult> RemovePasskey(Guid passkeyId, CancellationToken ct = default)
        => await SecurityGrain.RemovePasskeyAsync(passkeyId, ct);

    public async Task<ISetAutoDeleteResult> SetAutoDeletePeriod(int? months, CancellationToken ct = default)
        => await SecurityGrain.SetAutoDeletePeriodAsync(months, ct);

    public async Task<AutoDeletePeriod> GetAutoDeletePeriod(CancellationToken ct = default)
        => await SecurityGrain.GetAutoDeletePeriodAsync(ct);

    public async Task<SecurityDetails> GetSecurityDetails(CancellationToken ct = default)
        => await SecurityGrain.GetSecurityDetailsAsync(ct);
}