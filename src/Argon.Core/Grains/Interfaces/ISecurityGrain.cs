namespace Argon.Grains.Interfaces;

using Api.Features.CoreLogic.Otp;

[Alias("Argon.Grains.Interfaces.ISecurityGrain")]
public interface ISecurityGrain : IGrainWithGuidKey
{
    [Alias(nameof(RequestEmailChangeAsync))]
    Task<IRequestEmailChangeResult> RequestEmailChangeAsync(string newEmail, string password, CancellationToken ct = default);

    [Alias(nameof(ConfirmEmailChangeAsync))]
    Task<IConfirmEmailChangeResult> ConfirmEmailChangeAsync(string verificationCode, CancellationToken ct = default);

    [Alias(nameof(RequestPhoneChangeAsync))]
    Task<IRequestPhoneChangeResult> RequestPhoneChangeAsync(string newPhone, string password, CancellationToken ct = default);

    [Alias(nameof(ConfirmPhoneChangeAsync))]
    Task<IConfirmPhoneChangeResult> ConfirmPhoneChangeAsync(string verificationCode, CancellationToken ct = default);

    [Alias(nameof(RemovePhoneAsync))]
    Task<IRemovePhoneResult> RemovePhoneAsync(string password, CancellationToken ct = default);

    [Alias(nameof(ChangePasswordAsync))]
    Task<IChangePasswordResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default);

    [Alias(nameof(EnableOTPAsync))]
    Task<IEnableOTPResult> EnableOTPAsync(CancellationToken ct = default);

    [Alias(nameof(VerifyAndEnableOTPAsync))]
    Task<IVerifyOTPResult> VerifyAndEnableOTPAsync(string code, CancellationToken ct = default);

    [Alias(nameof(DisableOTPAsync))]
    Task<IDisableOTPResult> DisableOTPAsync(string code, CancellationToken ct = default);

    [Alias(nameof(GetPasskeysAsync))]
    Task<List<Passkey>> GetPasskeysAsync(CancellationToken ct = default);

    [Alias(nameof(BeginAddPasskeyAsync))]
    Task<IBeginPasskeyResult> BeginAddPasskeyAsync(string name, CancellationToken ct = default);

    [Alias(nameof(CompleteAddPasskeyAsync))]
    Task<ICompletePasskeyResult> CompleteAddPasskeyAsync(Guid passkeyId, string publicKey, CancellationToken ct = default);

    [Alias(nameof(RemovePasskeyAsync))]
    Task<IRemovePasskeyResult> RemovePasskeyAsync(Guid passkeyId, CancellationToken ct = default);

    [Alias(nameof(SetAutoDeletePeriodAsync))]
    Task<ISetAutoDeleteResult> SetAutoDeletePeriodAsync(int? months, CancellationToken ct = default);

    [Alias(nameof(GetAutoDeletePeriodAsync))]
    Task<AutoDeletePeriod> GetAutoDeletePeriodAsync(CancellationToken ct = default);

    [Alias(nameof(GetSecurityDetailsAsync))]
    Task<SecurityDetails> GetSecurityDetailsAsync(CancellationToken ct = default);
}
