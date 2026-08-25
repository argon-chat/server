namespace Argon.Api.Features.AccountConsole;

using AccountContracts;

/// <summary>
/// What a person can do to their own account from the console: see its state, schedule or cancel its
/// deletion, and ask for a GDPR export.
/// </summary>
public sealed class AccountConsoleService : IAccountConsole
{
    public async Task<MeDetails> GetMe(CancellationToken ct = default)
    {
        var user   = this.GetRequestContext();
        var userId = this.GetUserId();

        var exporting = await this.GetGrain<IUserDataExportGrain>(userId).IsExportInProgressAsync();
        var deletion  = await this.GetGrain<IAccountDeletionGrain>(userId).GetDeletionStatusAsync();

        return new MeDetails(
            exporting,
            Map(deletion.Status),
            deletion.ScheduledAt?.UtcDateTime,
            deletion.ExecutionAt?.UtcDateTime,
            Prop(user, "avatarId"),
            userId,
            Prop(user, "displayName"));
    }

    public async Task<DeleteAccountResult> RequestDeleteAccount(string password, CancellationToken ct = default)
    {
        var grain  = this.GetGrain<IAccountDeletionGrain>(this.GetUserId());
        var result = await grain.RequestDeletionAsync(password);

        if (!result.Success)
        {
            return new DeleteAccountResult(false, result.Error switch
            {
                AccountDeletionRequestError.InvalidPassword       => DeleteAccountError.InvalidPassword,
                AccountDeletionRequestError.AlreadyScheduled      => DeleteAccountError.AlreadyScheduled,
                AccountDeletionRequestError.HasActiveSubscription => DeleteAccountError.HasActiveSubscription,
                AccountDeletionRequestError.OwnsSpaces            => DeleteAccountError.OwnsSpaces,
                AccountDeletionRequestError.AccountLocked         => DeleteAccountError.AccountLocked,
                _                                                 => DeleteAccountError.InternalError
            }, null, null);
        }

        // The request result carries one timestamp — when deletion will run — so the pair the console
        // renders comes back from the status instead. Deriving the execution date here by adding a
        // fixed grace period would go wrong the moment GracePeriodDays is configured to anything else.
        var status = await grain.GetDeletionStatusAsync();

        return new DeleteAccountResult(true, DeleteAccountError.None,
            status.ScheduledAt?.UtcDateTime,
            status.ExecutionAt?.UtcDateTime ?? result.ScheduledDeletionAt?.UtcDateTime);
    }

    public async Task<CancelDeleteResult> CancelDeleteAccount(CancellationToken ct = default)
    {
        var result = await this.GetGrain<IAccountDeletionGrain>(this.GetUserId()).CancelDeletionAsync();

        if (result.Success)
            return new CancelDeleteResult(true, CancelDeleteError.None);

        return new CancelDeleteResult(false, result.Error switch
        {
            AccountDeletionCancelError.NotScheduled     => CancelDeleteError.NotScheduled,
            AccountDeletionCancelError.AlreadyExecuting => CancelDeleteError.AlreadyExecuting,
            AccountDeletionCancelError.AlreadyCompleted => CancelDeleteError.AlreadyCompleted,
            _                                           => CancelDeleteError.InternalError
        });
    }

    public async Task<RequestExportGDRPStatus> RequestExportGDRP(CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserDataExportGrain>(this.GetUserId()).RequestExportAsync();

        if (result.Success)
            return RequestExportGDRPStatus.Ok;

        return result.Error switch
        {
            ExportRequestError.AlreadyInProgress => RequestExportGDRPStatus.Already,
            ExportRequestError.RateLimited       => RequestExportGDRPStatus.RateLimit,
            _                                    => RequestExportGDRPStatus.Unknown
        };
    }

    /// <summary>
    /// Display fields come from the token, not from the database — the console never needs to load a
    /// user to render its own header.
    /// </summary>
    private static string Prop(ArgonRequestContextData context, string key)
        => context.Props.TryGetValue(key, out var value) ? value : string.Empty;

    private static DeletionStatusKind Map(AccountDeletionStatusKind status)
        => status switch
        {
            AccountDeletionStatusKind.None      => DeletionStatusKind.None,
            AccountDeletionStatusKind.Scheduled => DeletionStatusKind.Scheduled,
            AccountDeletionStatusKind.Executing => DeletionStatusKind.Executing,
            AccountDeletionStatusKind.Completed => DeletionStatusKind.Completed,
            AccountDeletionStatusKind.Failed    => DeletionStatusKind.Failed,
            _                                   => DeletionStatusKind.None
        };
}
