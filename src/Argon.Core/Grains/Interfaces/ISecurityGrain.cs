namespace Argon.Grains.Interfaces;

using Api.Features.CoreLogic.Otp;

[Alias("Argon.Grains.Interfaces.ISecurityGrain")]
public interface ISecurityGrain : IGrainWithGuidKey
{
    // flowId is a verified CHANGE_EMAIL flow of IVerificationGrain, begun from sessionId.

    [Alias(nameof(RequestEmailChangeAsync))]
    Task<IRequestEmailChangeResult> RequestEmailChangeAsync(Guid flowId, string newEmail, Guid sessionId, CancellationToken ct = default);

    [Alias(nameof(ConfirmEmailChangeAsync))]
    Task<IConfirmEmailChangeResult> ConfirmEmailChangeAsync(Guid flowId, string verificationCode, Guid sessionId, CancellationToken ct = default);

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
    Task<ICompletePasskeyResult> CompleteAddPasskeyAsync(string registrationResponse, CancellationToken ct = default);

    [Alias(nameof(RemovePasskeyAsync))]
    Task<IRemovePasskeyResult> RemovePasskeyAsync(Guid passkeyId, CancellationToken ct = default);

    [Alias(nameof(SetAutoDeletePeriodAsync))]
    Task<ISetAutoDeleteResult> SetAutoDeletePeriodAsync(int? months, CancellationToken ct = default);

    [Alias(nameof(GetAutoDeletePeriodAsync))]
    Task<AutoDeletePeriod> GetAutoDeletePeriodAsync(CancellationToken ct = default);

    [Alias(nameof(GetSecurityDetailsAsync))]
    Task<SecurityDetails> GetSecurityDetailsAsync(CancellationToken ct = default);

    [Alias(nameof(BeginValidatePasskeyAsync))]
    Task<IBeginPasskeyValidateResult> BeginValidatePasskeyAsync(CancellationToken ct = default);

    [Alias(nameof(CompleteValidatePasskeyAsync))]
    Task<ICompletePasskeyResult> CompleteValidatePasskeyAsync(string authenticationResponse, CancellationToken ct = default);

    // The caller's own sid is passed in rather than read off the request context: grains only receive
    // the ids ArgonOrleansInterceptor copies across, and there is no getter for the session id on that
    // side. Making it a parameter also keeps "which session am I" answerable in a test.

    [Alias(nameof(GetSessionsAsync))]
    Task<List<SessionInfo>> GetSessionsAsync(Guid currentSessionId, CancellationToken ct = default);

    [Alias(nameof(RevokeSessionAsync))]
    Task<IRevokeSessionResult> RevokeSessionAsync(Guid sessionId, Guid currentSessionId, CancellationToken ct = default);

    [Alias(nameof(RevokeAllSessionsAsync))]
    Task<IRevokeSessionResult> RevokeAllSessionsAsync(Guid currentSessionId, CancellationToken ct = default);

    // The same three with the caller's credential sid (the token's sid) as well, which is what names
    // the caller's own row on the registry; the presence sid alone changes on every launch. The
    // three above are kept for an older entrypoint and pass null.

    [Alias(nameof(ListSessionsAsync))]
    Task<List<SessionInfo>> ListSessionsAsync(Guid currentSessionId, Guid? currentCredentialSessionId, CancellationToken ct = default);

    [Alias(nameof(RevokeOneSessionAsync))]
    Task<IRevokeSessionResult> RevokeOneSessionAsync(Guid sessionId, Guid currentSessionId, Guid? currentCredentialSessionId, CancellationToken ct = default);

    [Alias(nameof(RevokeOtherSessionsAsync))]
    Task<IRevokeSessionResult> RevokeOtherSessionsAsync(Guid currentSessionId, Guid? currentCredentialSessionId, CancellationToken ct = default);
}
