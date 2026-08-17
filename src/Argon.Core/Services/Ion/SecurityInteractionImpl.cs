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

    public async Task<ICompletePasskeyResult> CompleteAddPasskey(string registrationResponse, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).CompleteAddPasskeyAsync(registrationResponse, ct);

    public async Task<IRemovePasskeyResult> RemovePasskey(Guid passkeyId, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RemovePasskeyAsync(passkeyId, ct);

    public async Task<ISetAutoDeleteResult> SetAutoDeletePeriod(int? months, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).SetAutoDeletePeriodAsync(months, ct);

    public async Task<AutoDeletePeriod> GetAutoDeletePeriod(CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).GetAutoDeletePeriodAsync(ct);

    public async Task<IRequestDataExportResult> RequestDataExport(CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserDataExportGrain>(this.GetUserId()).RequestExportAsync();

        if (result is { Success: true, ExportId: { } exportId })
            return new SuccessRequestDataExport(exportId);

        return new FailedRequestDataExport(result.Error switch
        {
            ExportRequestError.AlreadyInProgress => DataExportError.ALREADY_IN_PROGRESS,
            ExportRequestError.RateLimited       => DataExportError.RATE_LIMITED,
            ExportRequestError.NotConfigured     => DataExportError.NOT_CONFIGURED,
            _                                    => DataExportError.NONE
        });
    }

    /// <summary>
    /// Progress of the caller's export.
    /// </summary>
    /// <remarks>
    /// <c>FailureReason</c> is deliberately not carried across. It is written for an operator
    /// reading logs and can name storage paths and internal services; the client only needs to know
    /// that it failed and that asking again is allowed.
    /// </remarks>
    public async Task<DataExportStatus> GetDataExportStatus(CancellationToken ct = default)
    {
        var status = await this.GetGrain<IUserDataExportGrain>(this.GetUserId()).GetExportStatusAsync();

        return new DataExportStatus(
            status.Status switch
            {
                ExportStatusKind.Queued         => DataExportStatusKind.QUEUED,
                ExportStatusKind.CollectingData => DataExportStatusKind.COLLECTING,
                ExportStatusKind.Assembling     => DataExportStatusKind.ASSEMBLING,
                ExportStatusKind.Completed      => DataExportStatusKind.COMPLETED,
                ExportStatusKind.Expired        => DataExportStatusKind.EXPIRED,
                ExportStatusKind.Failed         => DataExportStatusKind.FAILED,
                _                               => DataExportStatusKind.IDLE
            },
            status.ExportId,
            status.StartedAt,
            status.CompletedAt,
            status.DownloadUrl,
            status.ItemsProcessed,
            status.TotalItemsEstimate);
    }

    public async Task CancelDataExport(CancellationToken ct = default)
        => await this.GetGrain<IUserDataExportGrain>(this.GetUserId()).CancelExportAsync();

    public async Task<SecurityDetails> GetSecurityDetails(CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).GetSecurityDetailsAsync(ct);

    public async Task<IBeginPasskeyValidateResult> BeginValidatePasskey(CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).BeginValidatePasskeyAsync(ct);

    public async Task<ICompletePasskeyResult> CompleteValidatePasskey(string authenticationResponse, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).CompleteValidatePasskeyAsync(authenticationResponse, ct);

    // GetSessionId() is read here rather than inside the grain: it is the caller's own session, and
    // the ion request context is the only place it exists.
    public async Task<IonArray<SessionInfo>> GetSessions(CancellationToken ct = default)
        => new(await this.GetGrain<ISecurityGrain>(this.GetUserId()).GetSessionsAsync(this.GetSessionId(), ct));

    public async Task<IRevokeSessionResult> RevokeSession(Guid sessionId, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RevokeSessionAsync(sessionId, this.GetSessionId(), ct);

    public async Task<IRevokeSessionResult> RevokeAllSessions(CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RevokeAllSessionsAsync(this.GetSessionId(), ct);
}