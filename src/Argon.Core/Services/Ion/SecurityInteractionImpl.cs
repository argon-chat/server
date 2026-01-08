namespace Argon.Services.Ion;

using ion.runtime;

public class SecurityInteractionImpl : ISecurityInteraction
{
    public async Task<IRequestEmailChangeResult> RequestEmailChange(string newEmail, string password, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RequestEmailChangeAsync(newEmail, password, ct);

    public async Task<IConfirmEmailChangeResult> ConfirmEmailChange(string verificationCode, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).ConfirmEmailChangeAsync(verificationCode, ct);

    public async Task<IRequestPhoneChangeResult> RequestPhoneChange(string newPhone, string password, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RequestPhoneChangeAsync(newPhone, password, ct);

    public async Task<IConfirmPhoneChangeResult> ConfirmPhoneChange(string verificationCode, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).ConfirmPhoneChangeAsync(verificationCode, ct);

    public async Task<IRemovePhoneResult> RemovePhone(string password, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RemovePhoneAsync(password, ct);

    public async Task<IChangePasswordResult> ChangePassword(string currentPassword, string newPassword, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).ChangePasswordAsync(currentPassword, newPassword, ct);

    public async Task<IEnableOTPResult> EnableOTP(CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).EnableOTPAsync(ct);

    public async Task<IVerifyOTPResult> VerifyAndEnableOTP(string code, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).VerifyAndEnableOTPAsync(code, ct);

    public async Task<IDisableOTPResult> DisableOTP(string code, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).DisableOTPAsync(code, ct);

    public async Task<IonArray<Passkey>> GetPasskeys(CancellationToken ct = default)
        => new(await this.GetGrain<ISecurityGrain>(this.GetUserId()).GetPasskeysAsync(ct));

    public async Task<IBeginPasskeyResult> BeginAddPasskey(string name, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).BeginAddPasskeyAsync(name, ct);

    public async Task<ICompletePasskeyResult> CompleteAddPasskey(Guid passkeyId, string publicKey, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).CompleteAddPasskeyAsync(passkeyId, publicKey, ct);

    public async Task<IRemovePasskeyResult> RemovePasskey(Guid passkeyId, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RemovePasskeyAsync(passkeyId, ct);

    public async Task<ISetAutoDeleteResult> SetAutoDeletePeriod(int? months, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).SetAutoDeletePeriodAsync(months, ct);

    public async Task<AutoDeletePeriod> GetAutoDeletePeriod(CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).GetAutoDeletePeriodAsync(ct);
}