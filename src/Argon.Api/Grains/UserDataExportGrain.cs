namespace Argon.Grains;

using System.IO.Compression;
using Argon.Core.Entities.Data;
using Argon.Features.Logic;
using Argon.Features.Storage;
using Instruments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Orleans.Configuration;
using Orleans.Providers;
using Persistence.States;

public class UserDataExportGrain(
    [PersistentState("user-data-export-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<UserDataExportGrainState> state,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IExportS3Service exportS3,
    IS3StorageService storageService,
    IGrainFactory grainFactory,
    IOptions<DataExportOptions> options,
    IOptions<StorageOptions> storageOptions,
    IOptions<ReminderOptions> reminderOptions,
    ILogger<UserDataExportGrain> logger) : Grain, IUserDataExportGrain, IRemindable
{
    private IDisposable? _processTimer;

    /// <summary>
    /// The reminder that deletes a finished archive once its lifetime is up.
    /// </summary>
    /// <remarks>
    /// A reminder rather than a timer because the deletion has to happen whether or not anybody is
    /// looking: the grain that completed an export is collected minutes later, and the object it
    /// wrote holds an e-mail address, a phone number, a date of birth, up to a hundred IP addresses
    /// and every message the person wrote. <c>export_ready.html</c> promises in writing that "after
    /// that, the archive will be permanently deleted", and this is what keeps that promise (defect
    /// X3). <see cref="CheckExpirationAsync"/> stays as the lazy fallback for the case where the
    /// reminder could not be registered at all.
    /// </remarks>
    private const string ArchiveExpiryReminder = "export-archive-ttl";

    /// <summary>The archive's own table of contents, and the one file assembly writes itself.</summary>
    private const string ManifestFile = "manifest.json";

    // The four clocks and the batch ceiling were static readonly fields here. They are one options
    // section now (DataExportOptions), with the same defaults to the second; see that class for why.
    private DataExportOptions Options => options.Value;

    private TimeSpan ProcessInterval  => Options.ProcessInterval;
    private TimeSpan FirstTickDelay   => Options.FirstTickDelay;
    private TimeSpan RateLimitPeriod  => Options.RateLimitPeriod;
    private TimeSpan ArchiveTtl       => Options.ArchiveTtl;
    private int      MessageBatchSize => Options.MessageBatchSize;

    /// <summary>Whether this deployment has an export store at all.</summary>
    /// <remarks>
    /// <para><c>StorageOptions.ExportBucketName</c> ships as an empty string and
    /// <c>ObjectStorageHealthCheck</c> deliberately filters an empty export bucket out of its probe,
    /// so "no export store" is a supported, healthy deployment shape rather than an outage — and one
    /// this grain has to be able to name, because every path past
    /// <see cref="RequestExportAsync"/> otherwise ends in <c>ExportS3Service</c> addressing a bucket
    /// called <c>""</c>: category PUTs that fail one at a time, then a listing that throws and fails
    /// the export a tick later, with the person told by mail that their archive is being prepared in
    /// between (finding F3). <c>ExportRequestError.NotConfigured</c> has been in the enum, and mapped
    /// on both surfaces, since before anything produced it; this is what produces it.</para>
    ///
    /// <para>Read off the options rather than asked of <see cref="IExportS3Service"/> only because
    /// the service has no such question yet. When it grows an <c>IsConfigured</c> this should
    /// delegate to it, so that the storage layer and its callers cannot disagree about whether there
    /// is a bucket.</para>
    /// </remarks>
    private bool ExportStorageConfigured => !string.IsNullOrWhiteSpace(storageOptions.Value.ExportBucketName);

    private Guid UserId => this.GetPrimaryKey();

    /// <summary>
    /// Picks up whatever the last activation left behind: an export mid-flight, or an archive whose
    /// lifetime is running.
    /// </summary>
    /// <remarks>
    /// The re-arm in the second branch is the other half of a registration that failed (finding
    /// R15). <see cref="ArmArchiveExpiryAsync"/> cannot fail a completed export over a reminder
    /// table that was briefly unavailable — the archive exists and the person already has the link —
    /// so the failure is logged and the export completes. Without this, that swallowed failure was
    /// permanent: nothing re-registers the reminder, nothing else ever reactivates the grain, and
    /// the zip stays in the bucket for ever against <c>export_ready.html</c>'s written promise. Any
    /// activation now re-arms it, which is what makes the two collectors — the durable reminder and
    /// the lazy <see cref="CheckExpirationAsync"/> — cover each other: a read wakes the grain and
    /// either expires the archive outright or gives it back its reminder.
    /// </remarks>
    /// <remarks>
    /// The expiry is attempted here and never allowed to fail the activation (finding F8). R15 turned
    /// that check into durable work — a lapsed archive is deleted from the store and the transition is
    /// persisted — and an activation that throws takes every call on the grain with it: the person
    /// cannot read their export status, and cannot ask for a new export either, because the grain
    /// cannot finish forgetting the old one. A storage provider that is briefly slow would therefore
    /// have closed Settings → Privacy for as long as it lasted. Both collectors retry on their own —
    /// the reminder fires again, and the read paths in <see cref="RequestExportAsync"/> and
    /// <see cref="GetExportStatusAsync"/> run the same check inside a call, where a failure is one
    /// failed request rather than a dead grain — so deferring is the strictly better outcome.
    /// </remarks>
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await state.ReadStateAsync(cancellationToken);

        try
        {
            await CheckExpirationAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not expire the archive of user {UserId} while activating the grain; the expiry " +
                "reminder and the next status read will each try again", UserId);
        }

        if (state.State.Status is ExportStatus.Queued or ExportStatus.CollectingData or ExportStatus.Assembling)
        {
            StartProcessingTimer();
            await NotifyPumpRegisteredAsync();
        }
        else if (state.State.Status is ExportStatus.Completed && state.State.ArchiveS3Key is { Length: > 0 })
            await EnsureArchiveExpiryArmedAsync();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _processTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts an export, or says why it will not.
    /// </summary>
    /// <remarks>
    /// The deletion guard is the third of the three refusals and the newest (defect ACC-11). An
    /// account whose erasure is scheduled, running or done must not be able to have a fresh,
    /// downloadable copy of everything it is about to lose assembled for it: the archive outlives
    /// the account by its own lifetime, and for an account already erased the collectors read
    /// through the soft-delete filter and would produce an archive describing nobody. The window is
    /// not a trap — cancelling the deletion lifts the refusal — and both surfaces can now name the
    /// reason: <c>DataExportError.ACCOUNT_DELETION_SCHEDULED</c> over ion and
    /// <c>RequestExportGDRPStatus.AccountDeletionScheduled</c> on the console.
    /// Pinned by <c>AccountDeletionTests.An_export_cannot_be_started_for_an_account_under_deletion</c>
    /// and <c>DataExportArchiveTests.An_account_under_deletion_is_refused_a_fresh_copy_of_itself</c>.
    /// </remarks>
    /// <remarks>
    /// <c>Failed</c> is refused alongside the other three, and it is the one that reads like an
    /// oversight until you follow it: a failed erasure is not an absent erasure, it is one part-way
    /// through. <c>AccountDeletionGrain.CheckAndExecuteAsync</c> resumes a <c>Failed</c> run from
    /// <c>StepsDone</c> while attempts remain, so an account passes <em>through</em> that state on
    /// its way out, and the two shapes of the open door are both bad. Before the row is anonymised
    /// the export assembles a complete archive of an intact account, and the resumed run then skips
    /// the archive purge it has already recorded — the erased account's e-mail, phone, date of birth
    /// and every message it wrote survive it behind a presigned link. After the row is anonymised
    /// the collectors read through the soft-delete filter, the export completes with a zip of empty
    /// files, and <c>LastExportCompletedAt</c> is stamped so the person's next genuine request is
    /// rate-limited (findings R4/R25).
    /// </remarks>
    public async ValueTask<ExportRequestResult> RequestExportAsync()
    {
        await CheckExpirationAsync();
        UserDataExportInstrument.ExportsRequested.Add(1);

        // Answered before the other three, because it is the only refusal that is about the
        // deployment rather than about this account: with no bucket there is nothing an
        // "already in progress" or a rate limit could usefully describe, and every other answer would
        // send the person away to try again later at a feature that cannot work at all. See
        // ExportStorageConfigured above for why an absent bucket is a deployment shape and not a fault.
        if (!ExportStorageConfigured)
        {
            logger.LogWarning(
                "Export request refused for user {UserId}: this deployment has no export bucket configured", UserId);

            return new ExportRequestResult
            {
                Success = false,
                Error   = ExportRequestError.NotConfigured
            };
        }

        if (state.State.Status is ExportStatus.Queued or ExportStatus.CollectingData or ExportStatus.Assembling)
        {
            logger.LogWarning("Export request rejected for user {UserId}: already in progress", UserId);
            return new ExportRequestResult
            {
                Success = false,
                Error   = ExportRequestError.AlreadyInProgress
            };
        }

        var deletion = await grainFactory.GetGrain<IAccountDeletionGrain>(UserId).GetDeletionStatusAsync();

        if (deletion.Status is AccountDeletionStatusKind.Scheduled
            or AccountDeletionStatusKind.Executing
            or AccountDeletionStatusKind.Failed
            or AccountDeletionStatusKind.Completed)
        {
            logger.LogWarning("Export request rejected for user {UserId}: deletion is {DeletionStatus}",
                UserId, deletion.Status);
            return new ExportRequestResult
            {
                Success = false,
                Error   = ExportRequestError.AccountDeletionScheduled
            };
        }

        if (state.State.LastExportCompletedAt is { } lastCompleted
            && DateTimeOffset.UtcNow - lastCompleted < RateLimitPeriod)
        {
            logger.LogWarning("Export request rate-limited for user {UserId}, last export: {LastExport}", UserId, lastCompleted);
            UserDataExportInstrument.ExportsRateLimited.Add(1);
            return new ExportRequestResult
            {
                Success = false,
                Error   = ExportRequestError.RateLimited
            };
        }

        // A previous archive is superseded the moment a new export starts, and the fields below are
        // about to stop naming it. Deleting it here is what keeps the grain's promise that the only
        // archive of an account is the one its state points at; without it the object would outlive
        // every reference to itself (defect X3).
        await DiscardArchiveAsync();

        var exportId = ArgonId.New();
        state.State.Status          = ExportStatus.Queued;
        state.State.CurrentExportId = exportId;
        state.State.StartedAt       = DateTimeOffset.UtcNow;
        state.State.CompletedAt     = null;
        state.State.FailureReason   = null;
        state.State.Cursor          = new ExportCursor();
        state.State.CategoryCounts  = new Dictionary<string, int>();
        state.State.ItemsProcessed  = 0;

        StartProcessingTimer();

        return await CompleteRequest(exportId);
    }

    private async ValueTask<ExportRequestResult> CompleteRequest(Guid exportId)
    {
        await state.WriteStateAsync();
        await NotifyPumpRegisteredAsync();

        logger.LogInformation("Data export started for user {UserId}, exportId={ExportId}", UserId, exportId);
        UserDataExportInstrument.ExportsStarted.Add(1);

        // Send "export started" email
        await SendExportStartedEmailAsync();

        return new ExportRequestResult
        {
            Success  = true,
            ExportId = exportId
        };
    }

    public async ValueTask<ExportStatusDto> GetExportStatusAsync()
    {
        await CheckExpirationAsync();
        return new ExportStatusDto
        {
            Status         = MapStatus(state.State.Status),
            ExportId       = state.State.CurrentExportId,
            StartedAt      = state.State.StartedAt,
            CompletedAt    = state.State.CompletedAt,
            DownloadUrl    = state.State.DownloadUrl,
            ItemsProcessed = state.State.ItemsProcessed,
            FailureReason  = state.State.FailureReason
        };
    }

    public async ValueTask<bool> IsExportInProgressAsync()
    {
        await CheckExpirationAsync();
        return state.State.Status is ExportStatus.Queued or ExportStatus.CollectingData or ExportStatus.Assembling;
    }

    /// <summary>
    /// Stops a running export and leaves nothing of it behind.
    /// </summary>
    /// <remarks>
    /// <para><b>The archive goes with the intermediates</b> (finding F6). Since R17 the archive key is
    /// persisted the instant the PUT succeeds, so there is a real window in which the state names a
    /// live object while the status still reads <c>Assembling</c> and a cancel is therefore accepted:
    /// an assembly that uploaded and then stopped — a silo that went away, a state write that did not
    /// land — is resumed from that key on the next activation, and either the account holder or step
    /// two of their own erasure can cancel in between. A reset that cleared the status but not
    /// <c>ArchiveS3Key</c> orphaned the object outright: <see cref="CheckExpirationAsync"/> returns
    /// for any status but <c>Completed</c>, <see cref="ReceiveReminder"/> cancels the reminder for any
    /// status but <c>Completed</c>, and a cancelled export never armed one — so a zip holding an
    /// e-mail address, a phone number, a date of birth and every message the account wrote survived
    /// with nothing anywhere naming it, until the account happened to request another export.</para>
    ///
    /// <para><see cref="DiscardArchiveAsync"/> is what <see cref="FailExportAsync"/> already does for
    /// the same state, so the two paths out of a running export now leave the same nothing behind. It
    /// runs before the reset because it nulls the key and the url in memory and the reset's write is
    /// what persists that.</para>
    ///
    /// <para><see cref="CancelArchiveExpiryAsync"/> is here for a reminder this export never armed:
    /// the one belonging to the account's <em>previous</em> archive. <see cref="RequestExportAsync"/>
    /// discards that archive when a new export starts but leaves its reminder standing, so a cancel
    /// can land with a reminder pointing at an object that is already gone. It would do no harm —
    /// <see cref="ReceiveReminder"/> cancels itself for any status but <c>Completed</c> — but it wakes
    /// this grain hours later for nothing, and cancelling it here is a line rather than a mechanism.</para>
    ///
    /// <para>The pump call is wrapped for the reason <see cref="FailExportAsync"/> gives: by the time
    /// it runs the export is stopped and persisted, and <c>IExportPumpGrain.UnregisterExportAsync</c>
    /// can wait a full request timeout when the pump's own tick is inside
    /// <c>IsExportInProgressAsync</c> on this grain. Failing the cancel over a stale pump entry would
    /// report a cancellation that did happen as an error — and the caller that most needs a truthful
    /// answer is <c>AccountDeletionGrain</c>'s step two, which deletes the export prefix immediately
    /// afterwards.</para>
    ///
    /// <para>Pinned by
    /// <c>DataExportArchiveTests.Cancelling_an_export_whose_archive_is_already_uploaded_destroys_it</c>.</para>
    /// </remarks>
    public async ValueTask CancelExportAsync()
    {
        if (state.State.Status is not (ExportStatus.Queued or ExportStatus.CollectingData or ExportStatus.Assembling))
            return;

        _processTimer?.Dispose();
        _processTimer = null;

        // Clean up intermediate files
        if (state.State.CurrentExportId is { } exportId)
        {
            var prefix = GetIntermediatePrefix(exportId);
            try { await exportS3.DeletePrefixAsync(prefix); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to clean intermediate files on cancel"); }
        }

        await DiscardArchiveAsync();

        state.State.Status         = ExportStatus.Idle;
        state.State.CurrentExportId = null;
        state.State.StartedAt      = null;
        state.State.Cursor         = new ExportCursor();
        state.State.ItemsProcessed = 0;
        await state.WriteStateAsync();
        await CancelArchiveExpiryAsync();

        try
        {
            await NotifyPumpUnregisteredAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not unregister the cancelled export of user {UserId} from the pump", UserId);
        }

        logger.LogInformation("Data export cancelled for user {UserId}", UserId);
        UserDataExportInstrument.ExportsCancelled.Add(1);
    }

    private void StartProcessingTimer()
    {
        _processTimer?.Dispose();
        _processTimer = this.RegisterGrainTimer(
            static async (grain, _) => await grain.ProcessTickAsync(),
            this,
            FirstTickDelay, // first tick fast
            ProcessInterval);
    }

    private async Task ProcessTickAsync()
    {
        var phase = state.State.Status.ToString();
        using var activity = UserDataExportInstrument.ActivitySource.StartActivity("Export.Tick");
        activity?.SetTag("export.user_id", UserId.ToString());
        activity?.SetTag("export.phase", phase);

        var sw = Stopwatch.StartNew();
        try
        {
            switch (state.State.Status)
            {
                case ExportStatus.Queued:
                    state.State.Status = ExportStatus.CollectingData;
                    await state.WriteStateAsync();
                    break;

                case ExportStatus.CollectingData:
                    await ProcessDataCollectionAsync();
                    break;

                case ExportStatus.Assembling:
                    await ProcessAssemblyAsync();
                    break;

                default:
                    _processTimer?.Dispose();
                    _processTimer = null;
                    break;
            }

            sw.Stop();
            UserDataExportInstrument.ExportTicksProcessed.Add(1,
                new KeyValuePair<string, object?>("phase", phase));
            UserDataExportInstrument.ExportTickDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("phase", phase));
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "Export tick failed for user {UserId}, export {ExportId}, phase {Phase}",
                UserId, state.State.CurrentExportId, phase);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            await FailExportAsync(ex.Message, ex.GetType().Name);
        }
    }

    #region Data Collection

    /// <summary>
    /// One step of collection per tick, in cursor order.
    /// </summary>
    /// <remarks>
    /// The five steps after subscriptions — friend requests, privacy rules and the ignore list,
    /// saved GIFs, passkeys and uploaded files — were missing entirely (defect X1). Every one of
    /// them is a table keyed on the user's own id that the same account can read in-app, two of them
    /// (saved GIFs, privacy rules) are not even erased by account deletion, and the export reported
    /// <c>Completed</c> without them and without saying anything was withheld. They are ordinary
    /// steps here rather than a special case, so the next category to be added is one more branch.
    /// Pinned by
    /// <c>DataExportArchiveTests.The_archive_accounts_for_the_personal_data_the_nine_files_leave_out</c>.
    /// </remarks>
    private async Task ProcessDataCollectionAsync()
    {
        var cursor   = state.State.Cursor;
        var exportId = state.State.CurrentExportId!.Value;

        if (!cursor.ProfileDone)
        {
            await CollectProfileAsync(exportId);
            cursor.ProfileDone = true;
        }
        else if (!cursor.FriendsDone)
        {
            await CollectFriendsAsync(exportId);
            cursor.FriendsDone = true;
        }
        else if (!cursor.BlocksDone)
        {
            await CollectBlocksAsync(exportId);
            cursor.BlocksDone = true;
        }
        else if (!cursor.SettingsDone)
        {
            await CollectSettingsAsync(exportId);
            cursor.SettingsDone = true;
        }
        else if (!cursor.StatsDone)
        {
            await CollectStatsAsync(exportId);
            cursor.StatsDone = true;
        }
        else if (!cursor.DevicesDone)
        {
            await CollectDevicesAsync(exportId);
            cursor.DevicesDone = true;
        }
        else if (!cursor.SubscriptionsDone)
        {
            await CollectSubscriptionsAsync(exportId);
            cursor.SubscriptionsDone = true;
        }
        else if (!cursor.FriendRequestsDone)
        {
            await CollectFriendRequestsAsync(exportId);
            cursor.FriendRequestsDone = true;
        }
        else if (!cursor.PrivacyDone)
        {
            await CollectPrivacyAsync(exportId);
            cursor.PrivacyDone = true;
        }
        else if (!cursor.SavedGifsDone)
        {
            await CollectSavedGifsAsync(exportId);
            cursor.SavedGifsDone = true;
        }
        else if (!cursor.PasskeysDone)
        {
            await CollectPasskeysAsync(exportId);
            cursor.PasskeysDone = true;
        }
        else if (!cursor.FilesDone)
        {
            await CollectFilesAsync(exportId);
            cursor.FilesDone = true;
        }
        else if (!cursor.DmConversationsDone)
        {
            await CollectDmBatchAsync(exportId);
        }
        else if (!cursor.ChannelMessagesDone)
        {
            await CollectChannelMessagesBatchAsync(exportId);
        }
        else
        {
            cursor.DataPhaseComplete = true;
            state.State.Status = ExportStatus.Assembling;
            logger.LogInformation("Data collection complete for user {UserId}, export {ExportId}. Moving to assembly",
                UserId, exportId);
        }

        state.State.ItemsProcessed++;
        await state.WriteStateAsync();
    }

    private async Task CollectProfileAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == UserId);

        if (user == null) return;

        var data = new
        {
            user.Id,
            user.Email,
            user.Username,
            user.DisplayName,
            user.PhoneNumber,
            user.DateOfBirth,
            user.CreatedAt,
            AvatarUrl = user.AvatarFileId != null
                ? storageService.GetDownloadUrl(user.AvatarFileId)
                : null,
            Profile = user.Profile != null ? new
            {
                user.Profile.CustomStatus,
                user.Profile.Bio,
                user.Profile.Badges,
                user.Profile.PrimaryColor,
                user.Profile.AccentColor
            } : null
        };

        await UploadJsonAsync(exportId, "profile.json", data, 1);
    }

    private async Task CollectFriendsAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var friends = await db.Friends
            .AsNoTracking()
            .Where(f => f.UserId == UserId)
            .Select(f => new { f.FriendId, f.CreatedAt })
            .ToListAsync();

        await UploadJsonAsync(exportId, "friends.json", friends, friends.Count);
    }

    private async Task CollectBlocksAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var blocks = await db.UserBlocklist
            .AsNoTracking()
            .Where(b => b.UserId == UserId)
            .Select(b => new { b.BlockedId, b.CreatedAt })
            .ToListAsync();

        await UploadJsonAsync(exportId, "blocks.json", blocks, blocks.Count);
    }

    private async Task CollectSettingsAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var muteSettings = await db.MuteSettings
            .AsNoTracking()
            .Where(m => m.UserId == UserId)
            .ToListAsync();

        var autoDelete = await db.AutoDeleteSettings
            .AsNoTracking()
            .Where(a => a.UserId == UserId)
            .FirstOrDefaultAsync();

        var data = new
        {
            MuteSettings = muteSettings.Select(m => new { m.TargetId, m.MuteLevel, m.MuteExpiresAt }),
            AutoDelete = autoDelete != null ? new { autoDelete.Enabled, autoDelete.Months } : null
        };

        await UploadJsonAsync(exportId, "settings.json", data, muteSettings.Count + (autoDelete is null ? 0 : 1));
    }

    private async Task CollectStatsAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var stats = await db.UserDailyStats
            .AsNoTracking()
            .Where(s => s.UserId == UserId)
            .OrderByDescending(s => s.Date)
            .Take(365)
            .Select(s => new { s.Date, s.TimeInVoiceSeconds, s.CallsMade, s.MessagesSent, s.XpEarned })
            .ToListAsync();

        var level = await db.UserLevels
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.UserId == UserId);

        var data = new
        {
            DailyStats = stats,
            Level = level != null ? new { level.CurrentLevel, level.TotalXpAllTime } : null
        };

        await UploadJsonAsync(exportId, "stats.json", data, stats.Count + (level is null ? 0 : 1));
    }

    private async Task CollectDevicesAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var devices = await db.DeviceHistories
            .AsNoTracking()
            .Where(d => d.UserId == UserId)
            .OrderByDescending(d => d.LastLoginTime)
            .Take(100)
            .Select(d => new { d.MachineId, d.DeviceType, d.LastKnownIP, d.LastLoginTime, d.AppId })
            .ToListAsync();

        await UploadJsonAsync(exportId, "devices.json", devices, devices.Count);
    }

    private async Task CollectSubscriptionsAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var subscription = await db.UltimaSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == UserId);

        var payments = await db.PaymentTransactions
            .AsNoTracking()
            .Where(p => p.UserId == UserId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Amount, p.Currency, p.Status, p.CreatedAt })
            .ToListAsync();

        var boosts = await db.SpaceBoosts
            .AsNoTracking()
            .Where(b => b.UserId == UserId)
            .Select(b => new { b.SpaceId, b.CreatedAt })
            .ToListAsync();

        var data = new
        {
            Subscription = subscription != null ? new { subscription.Tier, subscription.ExpiresAt, subscription.CreatedAt } : null,
            Payments = payments,
            Boosts = boosts
        };

        await UploadJsonAsync(exportId, "subscriptions.json", data, (subscription is null ? 0 : 1) + payments.Count + boosts.Count);
    }

    /// <summary>Pending friend requests, both directions.</summary>
    /// <remarks>
    /// Both halves are the account's own data — a request it sent names who it asked, one it
    /// received names who asked — and both are readable in-app through
    /// <c>FriendsGrain.GetMyFriendOutgoingListAsync</c> / <c>GetMyFriendPendingListAsync</c>. The
    /// direction is written out explicitly, because the pair of ids alone does not say who asked.
    /// </remarks>
    private async Task CollectFriendRequestsAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var requests = await db.FriendRequest
            .AsNoTracking()
            .Where(r => r.RequesterId == UserId || r.TargetId == UserId)
            .Select(r => new
            {
                Direction = r.RequesterId == UserId ? "Outgoing" : "Incoming",
                OtherPartyId = r.RequesterId == UserId ? r.TargetId : r.RequesterId,
                r.RequestedAt,
                r.ExpiredAt
            })
            .ToListAsync();

        await UploadJsonAsync(exportId, "friend-requests.json", requests, requests.Count);
    }

    /// <summary>Privacy rules and the ignore list — the account's "who may do X about me" policy.</summary>
    /// <remarks>
    /// One file for both because they are the same class of preference and neither is large.
    /// <c>PrivacyRules</c> also survives account deletion, so before this collector existed those
    /// rows were neither disclosed on request nor erased on request; the ignore list is the newest
    /// user-keyed table and was uncovered from the day it landed.
    /// </remarks>
    private async Task CollectPrivacyAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rules = await db.PrivacyRules
            .AsNoTracking()
            .Where(r => r.UserId == UserId)
            .Select(r => new { r.Id, r.Key, r.Mode, r.ScopeSpaceId, r.AllowExceptions, r.DenyExceptions, r.CreatedAt, r.UpdatedAt })
            .ToListAsync();

        var ignored = await db.UserIgnorelist
            .AsNoTracking()
            .Where(i => i.UserId == UserId)
            .Select(i => new { i.IgnoredId, i.CreatedAt })
            .ToListAsync();

        var data = new
        {
            Rules = rules,
            Ignored = ignored
        };

        await UploadJsonAsync(exportId, "privacy.json", data, rules.Count + ignored.Count);
    }

    private async Task CollectSavedGifsAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var gifs = await db.SavedGifs
            .AsNoTracking()
            .Where(g => g.UserId == UserId)
            .Select(g => new { g.Id, g.Slug, g.FileId, g.Width, g.Height, g.AddedAt, g.CreatedAt })
            .ToListAsync();

        await UploadJsonAsync(exportId, "saved-gifs.json", gifs, gifs.Count);
    }

    /// <summary>The passkeys registered on the account, as an inventory rather than as credentials.</summary>
    /// <remarks>
    /// Deliberately no <c>PublicKey</c> and no <c>CredentialId</c> bytes: what a person is owed
    /// under Art. 15 is the fact that a key exists, what they named it and when it was used, not
    /// the authenticator material itself, which is of no use to them and of some use to anybody
    /// else who reads the archive. Account deletion already treats these rows as personal data and
    /// hard-deletes them; the access side simply omitted them.
    /// </remarks>
    private async Task CollectPasskeysAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var passkeys = await db.Passkeys
            .AsNoTracking()
            .Where(k => k.UserId == UserId)
            .Select(k => new { k.Id, k.Name, k.SignCount, k.LastUsedAt, k.IsCompleted, k.CreatedAt })
            .ToListAsync();

        await UploadJsonAsync(exportId, "passkeys.json", passkeys, passkeys.Count);
    }

    /// <summary>Metadata of every file the account uploaded.</summary>
    /// <remarks>
    /// Metadata and not bytes, which is what <c>export_started.html</c> already tells the person
    /// ("media files will be included as links rather than direct downloads"): name, type, size,
    /// what it was uploaded for and when. The storage key is left out — it names our bucket layout,
    /// not their data — and so is a download link, which would have to be minted per row on a
    /// collection tick and would expire long before the archive does.
    /// </remarks>
    private async Task CollectFilesAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var files = await db.Files
            .AsNoTracking()
            .Where(f => f.OwnerId == UserId)
            .Select(f => new
            {
                f.Id,
                f.FileName,
                f.ContentType,
                f.FileSize,
                f.Purpose,
                f.SpaceId,
                f.ChannelId,
                f.CreatedAt
            })
            .ToListAsync();

        await UploadJsonAsync(exportId, "files.json", files, files.Count);
    }

    private async Task CollectDmBatchAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cursor = state.State.Cursor;

        var conversations = await db.UserConversations
            .AsNoTracking()
            .Where(c => c.UserId == UserId)
            .OrderBy(c => c.ConversationId)
            .Skip(cursor.DmConversationIndex)
            .Take(1)
            .ToListAsync();

        if (conversations.Count == 0)
        {
            cursor.DmConversationsDone = true;
            return;
        }

        var conv = conversations[0];
        var messages = await db.DirectMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conv.ConversationId && m.SenderId == UserId)
            .OrderBy(m => m.MessageId)
            .Select(m => new { m.MessageId, m.Text, m.Entities, m.CreatedAt })
            .ToListAsync();

        var data = new
        {
            conv.ConversationId,
            conv.PeerId,
            Messages = messages
        };

        await UploadJsonAsync(exportId, $"dm/{conv.ConversationId}.json", data, messages.Count);
        cursor.DmConversationIndex++;
    }

    /// <summary>One page of one channel of one space the account is a member of.</summary>
    /// <remarks>
    /// <para>Every dimension here is paged with an index and a <c>Take(1)</c> — spaces, then channels —
    /// except the messages themselves, which took the first <c>MessageBatchSize</c> and advanced.
    /// In production that handed a person their two hundred oldest messages per channel and nothing
    /// written since, with no marker anywhere that the rest existed (defect X2).</para>
    ///
    /// <para>Each page is its own object at a key derived from the cursor, and the pages are merged
    /// back into the one file per channel during assembly — the person opening the zip finds their
    /// channel, not a filing system. The first version merged in the store instead: it read the
    /// channel file back, appended the page and PUT it again, which is a read-modify-write of a
    /// shared object outside the state write that records it (finding R20). A silo drained after the
    /// PUT and before <c>WriteStateAsync</c> replayed the page against a file that already held it,
    /// so a channel of 450 messages exported 1-200, 201-400, 201-400 while the manifest — rolled back
    /// with the cursor — reported 400. A PUT at a deterministic key is a no-op when it repeats.</para>
    /// </remarks>
    private async Task CollectChannelMessagesBatchAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cursor = state.State.Cursor;

        // Get all space memberships for this user (non-deleted spaces)
        var memberships = await db.UsersToServerRelations
            .AsNoTracking()
            .Where(m => m.UserId == UserId)
            .Join(db.Spaces.Where(s => !s.IsDeleted), m => m.SpaceId, s => s.Id, (m, s) => new { m.SpaceId })
            .OrderBy(m => m.SpaceId)
            .Skip(cursor.SpaceMembershipIndex)
            .Take(1)
            .ToListAsync();

        if (memberships.Count == 0)
        {
            cursor.ChannelMessagesDone = true;
            return;
        }

        var spaceId = memberships[0].SpaceId;

        // Get channels for this space (non-deleted)
        var channels = await db.Channels
            .AsNoTracking()
            .Where(c => c.SpaceId == spaceId && !c.IsDeleted)
            .OrderBy(c => c.Id)
            .Skip(cursor.ChannelIndex)
            .Take(1)
            .ToListAsync();

        if (channels.Count == 0)
        {
            // Move to next space
            cursor.SpaceMembershipIndex++;
            cursor.ChannelIndex = 0;
            return;
        }

        var channel = channels[0];

        // One page of the caller's own messages in this channel. The Skip is what turns the old
        // ceiling into a pager: without it a channel contributed its oldest MessageBatchSize
        // messages and the cursor moved on for good (defect X2).
        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.ChannelId == channel.Id && m.CreatorId == UserId)
            .OrderBy(m => m.MessageId)
            .Skip(cursor.ChannelMessageOffset)
            .Take(MessageBatchSize)
            .Select(m => new { m.MessageId, m.Text, m.Entities, m.CreatedAt })
            .ToListAsync();

        var relativePath = $"channels/{spaceId}/{channel.Id}.json";

        if (messages.Count > 0)
        {
            if (cursor.ChannelMessageOffset == 0)
            {
                var data = new
                {
                    SpaceId = spaceId,
                    ChannelId = channel.Id,
                    ChannelName = channel.Name,
                    Messages = messages
                };

                await UploadJsonAsync(exportId, relativePath, data, messages.Count);
            }
            else
                await UploadPageAsync(exportId, relativePath, cursor.ChannelMessageOffset, messages);
        }

        // A full page means there may be more of this channel; page again on the next tick and leave
        // the channel index alone. Anything short of a full page is the end of it, and a channel that
        // ends exactly on a boundary costs one extra empty tick rather than a lost message.
        if (messages.Count == MessageBatchSize)
            cursor.ChannelMessageOffset += MessageBatchSize;
        else
        {
            cursor.ChannelMessageOffset = 0;
            cursor.ChannelIndex++;
        }
    }

    #endregion

    #region Assembly

    /// <summary>
    /// Zips what the collectors wrote, uploads it, and only then throws the working files away.
    /// </summary>
    /// <remarks>
    /// <para>The order of the last four steps is the whole of finding R14. It used to be: upload the
    /// archive, delete the intermediate prefix, presign, persist <c>Completed</c>. A silo drained (or
    /// an Orleans storage write that threw) inside that window left the persisted state saying
    /// <c>Assembling</c> with the intermediates already gone, so the next activation started the
    /// timer, re-entered here, wrote <c>manifest.json</c> into an empty prefix, listed exactly that
    /// one key — never tripping the empty guard — zipped it, and uploaded it <em>over</em> the good
    /// archive at the same date-stamped key. The person was mailed "your archive is ready" and
    /// downloaded a zip holding a manifest. Persisting the completion first makes that window
    /// harmless: either the state says <c>Assembling</c> and every intermediate is still there for an
    /// honest rebuild, or it says <c>Completed</c> and assembly never runs again.</para>
    ///
    /// <para>The key is persisted one write earlier still, immediately after the upload succeeds
    /// (finding R17). From the instant the PUT returns there is an object in the bucket holding an
    /// e-mail address, a phone number, a date of birth and every message the person wrote, and
    /// anything that throws afterwards routes into <see cref="FailExportAsync"/> — which can only
    /// delete an archive the state names. That write is also what makes the re-entry guard above
    /// truthful rather than decorative: a key in the state means the object is in the store.</para>
    ///
    /// <para>Everything after the completion write is best-effort on purpose. The export is finished
    /// and durable at that point; a store that will not delete the intermediates, or a pump that will
    /// not answer, must not be able to turn a completed export back into a failed one — which, since
    /// R17, would also delete the archive the person is being told about.</para>
    /// </remarks>
    private async Task ProcessAssemblyAsync()
    {
        var exportId = state.State.CurrentExportId!.Value;
        var prefix   = GetIntermediatePrefix(exportId);

        using var activity = UserDataExportInstrument.ActivitySource.StartActivity("Export.Assembly");
        activity?.SetTag("export.user_id", UserId.ToString());
        activity?.SetTag("export.export_id", exportId.ToString());

        var  archiveKey  = state.State.ArchiveS3Key;
        long archiveSize = 0;

        if (archiveKey is not { Length: > 0 })
        {
            // Listed before the manifest is written rather than after (finding R29). The manifest is
            // an object under the same prefix, so counting afterwards counted this method's own
            // output: the guard could only ever fire on a store that had lost the very key it had
            // just been given, while an export that collected nothing sailed past it and was handed
            // over as an archive of one file. The manifest is folded back in below so it still
            // travels in the same zip as the files it describes.
            var collected = await exportS3.ListObjectsAsync(prefix);

            if (collected.Count == 0)
            {
                logger.LogError("No intermediate data found for user {UserId}, export {ExportId}", UserId, exportId);
                await FailExportAsync("No data collected", "no_data");
                return;
            }

            await WriteManifestAsync(exportId);

            var manifestKey = $"{prefix}{ManifestFile}";
            var keys        = collected.Contains(manifestKey) ? collected : [.. collected, manifestKey];

            logger.LogInformation("Assembling archive for user {UserId}, export {ExportId}: {FileCount} intermediate files",
                UserId, exportId, keys.Count);

            using var zipStream = new MemoryStream();

            await WriteArchiveAsync(zipStream, prefix, keys);

            zipStream.Position = 0;
            archiveSize        = zipStream.Length;

            archiveKey = $"exports/{UserId}/{exportId}/export-{DateTime.UtcNow:yyyy-MM-dd}.zip";

            var uploaded = await exportS3.PutObjectAsync(archiveKey, zipStream, "application/zip");

            if (!uploaded)
            {
                logger.LogError("Failed to upload archive for user {UserId}, export {ExportId}", UserId, exportId);
                await FailExportAsync("Failed to upload archive", "upload_failed");
                return;
            }

            state.State.ArchiveS3Key = archiveKey;
            await state.WriteStateAsync();
        }
        else
            logger.LogWarning(
                "Resuming assembly for user {UserId}, export {ExportId}: the archive at {Key} is already uploaded",
                UserId, exportId, archiveKey);

        var downloadUrl = exportS3.GeneratePresignedGetUrl(archiveKey, (int)ArchiveTtl.TotalSeconds);

        state.State.Status                = ExportStatus.Completed;
        state.State.CompletedAt           = DateTimeOffset.UtcNow;
        state.State.LastExportCompletedAt = DateTimeOffset.UtcNow;
        state.State.DownloadUrl           = downloadUrl;

        _processTimer?.Dispose();
        _processTimer = null;
        await state.WriteStateAsync();

        try
        {
            await exportS3.DeletePrefixAsync(prefix);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "The archive of user {UserId} is complete but its intermediate files under {Prefix} could not be " +
                "removed; they hold the same personal data the archive does", UserId, prefix);
        }

        try
        {
            await NotifyPumpUnregisteredAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not unregister the completed export of user {UserId} from the pump", UserId);
        }

        await ArmArchiveExpiryAsync();

        var totalDuration = (DateTimeOffset.UtcNow - state.State.StartedAt!.Value).TotalSeconds;
        UserDataExportInstrument.ExportsCompleted.Add(1);
        UserDataExportInstrument.ExportDuration.Record(totalDuration);

        if (archiveSize > 0)
            UserDataExportInstrument.ExportArchiveSizeBytes.Record(archiveSize);

        logger.LogInformation(
            "Export completed for user {UserId}, export {ExportId}. Archive size: {ArchiveSize} bytes, duration: {Duration:F1}s",
            UserId, exportId, archiveSize, totalDuration);

        // Send "export ready" email
        await SendExportReadyEmailAsync(downloadUrl);
    }

    /// <summary>Turns the intermediate objects into the zip the person opens.</summary>
    /// <remarks>
    /// One entry per file the collectors wrote, except that a channel paged over several ticks
    /// arrives as a base object plus one <c>.partN.json</c> sibling per page and leaves as the single
    /// <c>channels/{space}/{channel}.json</c> the archive has always carried (finding R20, and the
    /// reason the pages can be written at deterministic keys at all). The merge is done here rather
    /// than in the collector because this is the one step that sees every page of a channel at once;
    /// the collector sees one.
    /// </remarks>
    private async Task WriteArchiveAsync(Stream destination, string prefix, IReadOnlyList<string> keys)
    {
        var pages = keys
           .Where(key => PagedMessages.IsPage(key))
           .GroupBy(PagedMessages.BaseKeyOf)
           .ToDictionary(group => group.Key, group => group.OrderBy(PagedMessages.OffsetOf).ToList());

        var merged = new HashSet<string>();

        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var key in keys.Where(key => !PagedMessages.IsPage(key)))
            {
                var relativePath = key[prefix.Length..];
                var objectStream = await exportS3.GetObjectStreamAsync(key);

                if (objectStream == null) continue;

                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                await using var entryStream = entry.Open();

                if (pages.TryGetValue(key, out var continuation))
                {
                    merged.Add(key);

                    await using (objectStream)
                        await MergePagesAsync(objectStream, continuation, entryStream);
                }
                else
                {
                    await using (objectStream)
                        await objectStream.CopyToAsync(entryStream);
                }
            }
        }

        // A page whose channel file is not in the listing has nowhere to be merged into, so it would
        // leave the archive silently short of a channel's later messages - the very failure the
        // paging exists to prevent. It cannot happen (offset zero writes the file before any page is
        // written), which is exactly why it is worth failing loudly rather than trusting.
        var orphaned = pages.Keys.Where(key => !merged.Contains(key)).ToList();

        if (orphaned.Count > 0)
            throw new InvalidOperationException(
                $"{orphaned.Count} paged channels have no file to merge into: {string.Join(", ", orphaned)}");
    }

    /// <summary>Appends every page of a channel to the file the first page opened.</summary>
    private async Task MergePagesAsync(Stream first, IReadOnlyList<string> pages, Stream destination)
    {
        var document = JObject.Parse(await ReadAllTextAsync(first));
        var messages = (JArray)document["Messages"]!;

        foreach (var page in pages)
        {
            var pageStream = await exportS3.GetObjectStreamAsync(page);

            if (pageStream is null)
                throw new InvalidOperationException($"The archive is missing the page '{page}' of a channel it exports");

            await using (pageStream)
            {
                foreach (var message in JArray.Parse(await ReadAllTextAsync(pageStream)))
                    messages.Add(message);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(document.ToString(Formatting.Indented));

        await destination.WriteAsync(bytes);
    }

    private static async Task<string> ReadAllTextAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        return await reader.ReadToEndAsync();
    }

    #endregion

    #region Helpers

    private string GetIntermediatePrefix(Guid exportId)
        => $"exports/{UserId}/{exportId}/intermediate/";

    /// <summary>Writes one file of the archive and records what it holds for the manifest.</summary>
    private async Task UploadJsonAsync(Guid exportId, string relativePath, object data, int items)
    {
        await PutJsonAsync(exportId, relativePath, data);
        RecordCategory(relativePath, items);
    }

    private async Task PutJsonAsync(Guid exportId, string relativePath, object data)
    {
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        var key  = $"{GetIntermediatePrefix(exportId)}{relativePath}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await exportS3.PutObjectAsync(key, stream, "application/json");
    }

    /// <summary>Writes one page of a channel's messages as an object of its own.</summary>
    /// <remarks>
    /// The key carries the cursor offset the page was read at, so writing the same page twice - the
    /// only thing a replay can do - overwrites it with itself rather than appending it again. The
    /// count is recorded against the channel's own path, because that is the file the page ends up
    /// in and the manifest names files, not the store's working objects.
    /// </remarks>
    private async Task UploadPageAsync(Guid exportId, string relativePath, int offset, object messages)
    {
        var page = JArray.FromObject(messages);

        await PutJsonAsync(exportId, PagedMessages.PagePathOf(relativePath, offset), page);

        RecordCategory(relativePath, page.Count);
    }

    /// <summary>Where a channel's pages live between the collector and the zip.</summary>
    /// <remarks>
    /// The naming is load-bearing in two directions: assembly has to be able to tell a page from a
    /// file (a page must never become an entry of its own) and to put the pages of one channel back
    /// in cursor order. Both are read off the key, because the state that knew the order was
    /// persisted per tick and the assembly step runs after all of them.
    /// </remarks>
    private static class PagedMessages
    {
        private const string Marker = ".part";
        private const string Suffix = ".json";

        public static string PagePathOf(string relativePath, int offset)
            => $"{relativePath[..^Suffix.Length]}{Marker}{offset}{Suffix}";

        public static bool IsPage(string key)
            => OffsetOrNull(key) is not null;

        public static int OffsetOf(string key)
            => OffsetOrNull(key) ?? throw new InvalidOperationException($"'{key}' is not a page of a channel");

        public static string BaseKeyOf(string key)
            => $"{key[..key.LastIndexOf(Marker, StringComparison.Ordinal)]}{Suffix}";

        private static int? OffsetOrNull(string key)
        {
            if (!key.EndsWith(Suffix, StringComparison.Ordinal))
                return null;

            var marker = key.LastIndexOf(Marker, StringComparison.Ordinal);

            if (marker < 0)
                return null;

            var digits = key[(marker + Marker.Length)..^Suffix.Length];

            return digits.Length > 0 && int.TryParse(digits, out var offset) ? offset : null;
        }
    }

    private void RecordCategory(string relativePath, int items)
    {
        var counts = state.State.CategoryCounts;

        counts[relativePath] = counts.TryGetValue(relativePath, out var already) ? already + items : items;
    }

    /// <summary>The archive's own table of contents.</summary>
    /// <remarks>
    /// Completeness is the one thing an Art. 15 response is judged on that neither the status nor
    /// the zip could express: the export said <c>Completed</c> whether it had written twelve files
    /// or four, and an empty category and a missing collector produced the same silence (defect X1).
    /// The manifest is written from what the collectors actually recorded rather than from a list of
    /// what they were supposed to write, so a category that failed to run is visible by its absence
    /// here too.
    /// </remarks>
    private async Task WriteManifestAsync(Guid exportId)
    {
        var manifest = new
        {
            ExportId    = exportId,
            UserId,
            GeneratedAt = DateTimeOffset.UtcNow,
            Categories  = state.State.CategoryCounts
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new { File = entry.Key, Items = entry.Value })
                .ToList()
        };

        await PutJsonAsync(exportId, ManifestFile, manifest);
    }

    /// <summary>
    /// The one way an export ends in <see cref="ExportStatus.Failed"/>.
    /// </summary>
    /// <remarks>
    /// <para>Three call sites used to write the same six lines and all three of them stopped short
    /// of the two things a failure owes anybody. The intermediates - <c>profile.json</c> with an
    /// e-mail, a phone number and a date of birth, <c>devices.json</c> with a hundred IP addresses -
    /// stayed in the bucket with nothing in the grain still naming them, while the cancel path a few
    /// lines above deleted exactly the same prefix (defect X4). And the person was told "we've
    /// started preparing your archive... you don't need to do anything else" and then never told
    /// that it would not arrive (defect X7).</para>
    ///
    /// <para>Order matters: the state write comes first, so a store outage - a likely cause of the
    /// failure in the first place - cannot stop <c>Failed</c> from being persisted, and every
    /// clean-up below is best-effort for the same reason.</para>
    ///
    /// <para>The archive goes with them (finding R17). Assembly can fail <em>after</em> the upload
    /// has succeeded — the presign, the prefix delete and the state write are all after it — and a
    /// failure that cleaned only the intermediates left the finished zip at
    /// <c>exports/{user}/{export}/export-*.zip</c> with no status naming it, no expiry reminder armed
    /// for it and no lifecycle rule over it. <see cref="DiscardArchiveAsync"/> deletes by the stored
    /// key, which is why <see cref="ProcessAssemblyAsync"/> persists that key the moment the object
    /// exists; the second state write is what makes the forgetting durable, and if it is the thing
    /// that failed the key survives for the next request's <see cref="DiscardArchiveAsync"/> to
    /// find.</para>
    ///
    /// <para><b>Every clean-up means every clean-up</b> (finding F7). The pump call was the one line
    /// here that was not wrapped, and it is the one with a documented stall: a
    /// <c>IExportPumpGrain.UnregisterExportAsync</c> issued while the pump's own tick is inside
    /// <c>IsExportInProgressAsync</c> on this very grain waits out a full request timeout and then
    /// throws. Unwrapped it cost the two things the method exists for. The failure mail below never
    /// ran, so a person told "we'll notify you when it's ready" was told nothing — defect X7 back on
    /// the path written to close it. And because <see cref="ProcessAssemblyAsync"/> calls this from
    /// inside <see cref="ProcessTickAsync"/>'s <c>try</c>, the throw landed in that catch, which
    /// called this method a second time: a repeated prefix delete, a second pump call, and the
    /// exception escaping a timer callback in the end.</para>
    ///
    /// <para>Pinned by <c>DataExportArchiveTests.A_failed_export_does_not_leave_its_intermediate_files_behind</c>
    /// and <c>DataExportArchiveTests.The_person_who_asked_for_an_export_is_told_when_it_fails</c>.</para>
    /// </remarks>
    private async Task FailExportAsync(string reason, string metricReason)
    {
        var exportId = state.State.CurrentExportId;

        _processTimer?.Dispose();
        _processTimer = null;

        state.State.Status        = ExportStatus.Failed;
        state.State.FailureReason = reason;
        await state.WriteStateAsync();

        if (state.State.ArchiveS3Key is { Length: > 0 })
        {
            await DiscardArchiveAsync();

            try
            {
                await state.WriteStateAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "The failed export of user {UserId} discarded its archive but could not forget the key", UserId);
            }
        }

        if (exportId is { } id)
        {
            try
            {
                await exportS3.DeletePrefixAsync(GetIntermediatePrefix(id));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean intermediate files on failure for user {UserId}", UserId);
            }
        }

        try
        {
            await NotifyPumpUnregisteredAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not unregister the failed export of user {UserId} from the pump", UserId);
        }

        UserDataExportInstrument.ExportsFailed.Add(1,
            new KeyValuePair<string, object?>("reason", metricReason));

        await SendExportFailedEmailAsync();
    }

    /// <summary>
    /// The lazy half of the archive's lifetime: a read notices the window has passed.
    /// </summary>
    /// <remarks>
    /// The durable half is <see cref="ArchiveExpiryReminder"/>, which fires whether or not anybody
    /// is looking. This stays as the fallback for a grain activated by a status poll after its
    /// reminder was lost, and it now does what the reminder does - delete the object and persist the
    /// transition - rather than only nulling the fields in memory and recomputing the same answer on
    /// every read (defect X3).
    /// </remarks>
    private async Task CheckExpirationAsync()
    {
        if (state.State.Status != ExportStatus.Completed)
            return;

        if (state.State.CompletedAt is not { } completed || DateTimeOffset.UtcNow - completed <= ArchiveTtl)
            return;

        await ExpireArchiveAsync();
    }

    private async Task ExpireArchiveAsync()
    {
        await DiscardArchiveAsync();

        state.State.Status = ExportStatus.Expired;
        await state.WriteStateAsync();
        await CancelArchiveExpiryAsync();

        logger.LogInformation("Export archive expired and removed for user {UserId}", UserId);
    }

    /// <summary>Deletes the archive object, if there still is one, and forgets it.</summary>
    /// <remarks>
    /// Best-effort on the store and unconditional on the state: an object the store would not delete
    /// is a leak worth a log line, but leaving a URL in the state that the product has said is dead
    /// would be worse. By the stored key rather than by prefix on purpose - the same prefix also
    /// holds the intermediates of an export that may be running right now.
    /// </remarks>
    private async Task DiscardArchiveAsync()
    {
        if (state.State.ArchiveS3Key is { Length: > 0 } key)
        {
            try
            {
                await exportS3.DeleteObjectAsync(key);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete export archive {Key} for user {UserId}", key, UserId);
            }
        }

        state.State.ArchiveS3Key = null;
        state.State.DownloadUrl  = null;
    }

    /// <summary>
    /// Arms the reminder that deletes the archive when its lifetime is up.
    /// </summary>
    /// <remarks>
    /// <para>The period is the archive's lifetime, floored at Orleans' own
    /// <c>ReminderOptions.MinimumReminderPeriod</c> - a minute out of the box, which the integration
    /// host lowers so a forty-second archive can be expired inside a test. A registration that fails
    /// is logged and swallowed rather than failing a completed export: the archive is built and the
    /// person already has the link, and <see cref="CheckExpirationAsync"/> still expires it the next
    /// time anybody reads the status.</para>
    ///
    /// <para>Retried before it is swallowed, though (finding R15). The realistic cause is a moment
    /// of unavailability in the reminder table - a lease moving, a deploy - which is over in
    /// seconds, and the cost of losing the race was an archive nothing would ever collect. What is
    /// swallowed is logged at Error rather than Warning, because it is the one outcome that leaves
    /// personal data in the bucket on a promise the product has already made in writing, and
    /// <see cref="OnActivateAsync"/> re-arms it on the next activation.</para>
    /// </remarks>
    private async Task ArmArchiveExpiryAsync()
    {
        var floor  = reminderOptions.Value.MinimumReminderPeriod;
        var period = ArchiveTtl < floor ? floor : ArchiveTtl;

        const int attempts = 3;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await this.RegisterOrUpdateReminder(ArchiveExpiryReminder, ArchiveTtl, period);
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                logger.LogWarning(ex,
                    "Could not arm the archive expiry reminder for user {UserId} (attempt {Attempt} of {Attempts})",
                    UserId, attempt, attempts);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Gave up arming the archive expiry reminder for user {UserId} after {Attempts} attempts; " +
                    "the archive at {Key} is now collected only by a status read waking the grain",
                    UserId, attempts, state.State.ArchiveS3Key);

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
        }
    }

    /// <summary>Gives a live archive its reminder back if it does not have one.</summary>
    /// <remarks>
    /// <c>RegisterOrUpdateReminder</c> is idempotent, so the read is not needed for correctness - it
    /// is there so that the normal activation of a grain holding a live archive costs a lookup
    /// rather than a write to the reminder table, and so that a re-arm is visible in the log as the
    /// exception it is.
    /// </remarks>
    private async Task EnsureArchiveExpiryArmedAsync()
    {
        try
        {
            if (await this.GetReminder(ArchiveExpiryReminder) is not null)
                return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the archive expiry reminder of user {UserId}", UserId);
        }

        logger.LogInformation(
            "Re-arming the archive expiry reminder for user {UserId}: the archive is live and has no reminder", UserId);

        await ArmArchiveExpiryAsync();
    }

    private async Task CancelArchiveExpiryAsync()
    {
        try
        {
            if (await this.GetReminder(ArchiveExpiryReminder) is { } reminder)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cancel the archive expiry reminder for user {UserId}", UserId);
        }
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ArchiveExpiryReminder)
            return;

        // Anything but a live archive means the reminder has outlived what it was armed for: a new
        // export, a cancellation, or an expiry a status read got to first.
        if (state.State.Status != ExportStatus.Completed)
        {
            await CancelArchiveExpiryAsync();
            return;
        }

        if (state.State.CompletedAt is { } completed && DateTimeOffset.UtcNow - completed >= ArchiveTtl)
            await ExpireArchiveAsync();
    }

    private async Task SendExportStartedEmailAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == UserId)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstOrDefaultAsync();

            if (user == null) return;

            var emailGrain = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
            await emailGrain.SendExportStartedAsync(user.Email, user.DisplayName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send export started email for user {UserId}", UserId);
        }
    }

    private async Task SendExportReadyEmailAsync(string downloadUrl)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == UserId)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstOrDefaultAsync();

            if (user == null) return;

            var emailGrain = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
            await emailGrain.SendExportReadyAsync(user.Email, user.DisplayName, downloadUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send export ready email for user {UserId}", UserId);
        }
    }

    /// <summary>
    /// Tells the person their archive is not coming.
    /// </summary>
    /// <remarks>
    /// The started mail promises a second one ("you don't need to do anything else - we'll notify
    /// you when it's ready") and until this existed there was no third outcome: a tick that threw
    /// left the account waiting on a subject-access request forever, with the console's only bit,
    /// <c>gdrpExportInProgress</c>, back to false and indistinguishable from never having asked
    /// (defect X7). <c>FailureReason</c> is deliberately not in the mail - it is written for an
    /// operator and names storage paths and internal services.
    /// </remarks>
    private async Task SendExportFailedEmailAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == UserId)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstOrDefaultAsync();

            if (user == null) return;

            var emailGrain = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
            await emailGrain.SendExportFailedAsync(user.Email, user.DisplayName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send export failed email for user {UserId}", UserId);
        }
    }

    private static ExportStatusKind MapStatus(ExportStatus status) => status switch
    {
        ExportStatus.Idle           => ExportStatusKind.Idle,
        ExportStatus.Queued         => ExportStatusKind.Queued,
        ExportStatus.CollectingData => ExportStatusKind.CollectingData,
        ExportStatus.Assembling     => ExportStatusKind.Assembling,
        ExportStatus.Completed      => ExportStatusKind.Completed,
        ExportStatus.Expired        => ExportStatusKind.Expired,
        ExportStatus.Failed         => ExportStatusKind.Failed,
        _                           => ExportStatusKind.Idle
    };

    #endregion

    #region Pump

    private ValueTask NotifyPumpRegisteredAsync()
        => grainFactory.GetGrain<IExportPumpGrain>(IExportPumpGrain.SingletonId)
            .RegisterActiveExportAsync(UserId);

    private ValueTask NotifyPumpUnregisteredAsync()
        => grainFactory.GetGrain<IExportPumpGrain>(IExportPumpGrain.SingletonId)
            .UnregisterExportAsync(UserId);

    #endregion
}
