namespace Argon.Services.Ion;

using ion.runtime;

public class SecurityInteractionImpl : ISecurityInteraction
{
    // What a verification flow is bound to: the token's sid, which survives a relaunch, or the presence sid
    // for a token from before the claim existed.
    private Guid FlowSession => this.GetCredentialSessionId() ?? this.GetSessionId();

    public async Task<IBeginVerificationResult> BeginVerification(SensitiveAction action, CancellationToken ct = default)
        => await this.GetGrain<IVerificationGrain>(this.GetUserId()).BeginAsync(action, FlowSession, ct);

    public async Task<IChallengeVerificationResult> ChallengeVerification(Guid flowId, VerificationFactor factor, CancellationToken ct = default)
        => await this.GetGrain<IVerificationGrain>(this.GetUserId()).ChallengeAsync(flowId, factor, FlowSession, ct);

    public async Task<ISubmitVerificationResult> SubmitVerification(Guid flowId, VerificationFactor factor, string proof, CancellationToken ct = default)
        => await this.GetGrain<IVerificationGrain>(this.GetUserId()).SubmitAsync(flowId, factor, proof, FlowSession, ct);

    public async Task CancelVerification(Guid flowId, CancellationToken ct = default)
        => await this.GetGrain<IVerificationGrain>(this.GetUserId()).CancelAsync(flowId, FlowSession, ct);

    public async Task<IRequestEmailChangeResult> RequestEmailChange(Guid flowId, string newEmail, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).RequestEmailChangeAsync(flowId, newEmail, FlowSession, ct);

    public async Task<IConfirmEmailChangeResult> ConfirmEmailChange(Guid flowId, string verificationCode, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId()).ConfirmEmailChangeAsync(flowId, verificationCode, FlowSession, ct);

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
            ExportRequestError.AlreadyInProgress       => DataExportError.ALREADY_IN_PROGRESS,
            ExportRequestError.RateLimited             => DataExportError.RATE_LIMITED,
            ExportRequestError.NotConfigured           => DataExportError.NOT_CONFIGURED,
            ExportRequestError.AccountDeletionScheduled => DataExportError.ACCOUNT_DELETION_SCHEDULED,
            _                                          => DataExportError.NONE
        });
    }

    /// <summary>
    /// Progress of the caller's export.
    /// </summary>
    /// <remarks>
    /// <para><c>FailureReason</c> is deliberately not carried across. It is written for an operator
    /// reading logs and can name storage paths and internal services; the client only needs to know
    /// that it failed and that asking again is allowed.</para>
    ///
    /// <para><c>totalItemsEstimate</c> used to be carried across too. It was assigned zero once per
    /// request and never computed, so every export reported "N of 0" to any client that trusted the
    /// name; it is gone from the contract rather than made honest (defect X8), because the step
    /// total is only knowable once the conversation and channel cursors are seeded - several ticks
    /// after the point a progress bar would have wanted it.</para>
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
            status.ItemsProcessed);
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
        => new(await this.GetGrain<ISecurityGrain>(this.GetUserId())
           .ListSessionsAsync(this.GetSessionId(), this.GetCredentialSessionId(), ct));

    public async Task<IRevokeSessionResult> RevokeSession(Guid sessionId, CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId())
           .RevokeOneSessionAsync(sessionId, this.GetSessionId(), this.GetCredentialSessionId(), ct);

    public async Task<IRevokeSessionResult> RevokeAllSessions(CancellationToken ct = default)
        => await this.GetGrain<ISecurityGrain>(this.GetUserId())
           .RevokeOtherSessionsAsync(this.GetSessionId(), this.GetCredentialSessionId(), ct);
}