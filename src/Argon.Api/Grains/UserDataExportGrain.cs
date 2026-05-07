namespace Argon.Grains;

using System.IO.Compression;
using Argon.Core.Entities.Data;
using Argon.Features.Storage;
using Instruments;
using Microsoft.EntityFrameworkCore;
using Orleans.Providers;
using Persistence.States;

public class UserDataExportGrain(
    [PersistentState("user-data-export-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<UserDataExportGrainState> state,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IExportS3Service exportS3,
    IS3StorageService storageService,
    IGrainFactory grainFactory,
    ILogger<UserDataExportGrain> logger) : Grain, IUserDataExportGrain
{
    private IDisposable? _processTimer;
    private static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan ArchiveTtl      = TimeSpan.FromHours(48);
    private const int MessageBatchSize = 200;

    private Guid UserId => this.GetPrimaryKey();

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await state.ReadStateAsync(cancellationToken);
        CheckExpiration();

        if (state.State.Status is ExportStatus.Queued or ExportStatus.CollectingData or ExportStatus.Assembling)
        {
            StartProcessingTimer();
            await NotifyPumpRegisteredAsync();
        }
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _processTimer?.Dispose();
        return Task.CompletedTask;
    }

    public ValueTask<ExportRequestResult> RequestExportAsync()
    {
        CheckExpiration();
        UserDataExportInstrument.ExportsRequested.Add(1);

        if (state.State.Status is ExportStatus.Queued or ExportStatus.CollectingData or ExportStatus.Assembling)
        {
            logger.LogWarning("Export request rejected for user {UserId}: already in progress", UserId);
            return ValueTask.FromResult(new ExportRequestResult
            {
                Success = false,
                Error   = ExportRequestError.AlreadyInProgress
            });
        }

        if (state.State.LastExportCompletedAt is { } lastCompleted
            && DateTimeOffset.UtcNow - lastCompleted < RateLimitPeriod)
        {
            logger.LogWarning("Export request rate-limited for user {UserId}, last export: {LastExport}", UserId, lastCompleted);
            UserDataExportInstrument.ExportsRateLimited.Add(1);
            return ValueTask.FromResult(new ExportRequestResult
            {
                Success = false,
                Error   = ExportRequestError.RateLimited
            });
        }

        var exportId = Guid.NewGuid();
        state.State.Status          = ExportStatus.Queued;
        state.State.CurrentExportId = exportId;
        state.State.StartedAt       = DateTimeOffset.UtcNow;
        state.State.CompletedAt     = null;
        state.State.ArchiveS3Key    = null;
        state.State.DownloadUrl     = null;
        state.State.FailureReason   = null;
        state.State.Cursor          = new ExportCursor();
        state.State.ItemsProcessed  = 0;
        state.State.TotalItemsEstimate = 0;

        StartProcessingTimer();

        return CompleteRequest(exportId);
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

    public ValueTask<ExportStatusDto> GetExportStatusAsync()
    {
        CheckExpiration();
        return ValueTask.FromResult(new ExportStatusDto
        {
            Status             = MapStatus(state.State.Status),
            ExportId           = state.State.CurrentExportId,
            StartedAt          = state.State.StartedAt,
            CompletedAt        = state.State.CompletedAt,
            DownloadUrl        = state.State.DownloadUrl,
            ItemsProcessed     = state.State.ItemsProcessed,
            TotalItemsEstimate = state.State.TotalItemsEstimate,
            FailureReason      = state.State.FailureReason
        });
    }

    public ValueTask<bool> IsExportInProgressAsync()
    {
        CheckExpiration();
        return ValueTask.FromResult(
            state.State.Status is ExportStatus.Queued or ExportStatus.CollectingData or ExportStatus.Assembling);
    }

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

        state.State.Status         = ExportStatus.Idle;
        state.State.CurrentExportId = null;
        state.State.StartedAt      = null;
        state.State.Cursor         = new ExportCursor();
        state.State.ItemsProcessed = 0;
        await state.WriteStateAsync();
        await NotifyPumpUnregisteredAsync();

        logger.LogInformation("Data export cancelled for user {UserId}", UserId);
        UserDataExportInstrument.ExportsCancelled.Add(1);
    }

    private void StartProcessingTimer()
    {
        _processTimer?.Dispose();
        _processTimer = this.RegisterGrainTimer(
            static async (grain, _) => await grain.ProcessTickAsync(),
            this,
            TimeSpan.FromSeconds(1), // first tick fast
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

            state.State.Status        = ExportStatus.Failed;
            state.State.FailureReason = ex.Message;
            _processTimer?.Dispose();
            _processTimer = null;
            await state.WriteStateAsync();
            await NotifyPumpUnregisteredAsync();

            UserDataExportInstrument.ExportsFailed.Add(1,
                new KeyValuePair<string, object?>("reason", ex.GetType().Name));
        }
    }

    #region Data Collection

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

        await UploadJsonAsync(exportId, "profile.json", data);
    }

    private async Task CollectFriendsAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var friends = await db.Friends
            .AsNoTracking()
            .Where(f => f.UserId == UserId)
            .Select(f => new { f.FriendId, f.CreatedAt })
            .ToListAsync();

        await UploadJsonAsync(exportId, "friends.json", friends);
    }

    private async Task CollectBlocksAsync(Guid exportId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var blocks = await db.UserBlocklist
            .AsNoTracking()
            .Where(b => b.UserId == UserId)
            .Select(b => new { b.BlockedId, b.CreatedAt })
            .ToListAsync();

        await UploadJsonAsync(exportId, "blocks.json", blocks);
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

        await UploadJsonAsync(exportId, "settings.json", data);
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

        await UploadJsonAsync(exportId, "stats.json", data);
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

        await UploadJsonAsync(exportId, "devices.json", devices);
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

        await UploadJsonAsync(exportId, "subscriptions.json", data);
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

        await UploadJsonAsync(exportId, $"dm/{conv.ConversationId}.json", data);
        cursor.DmConversationIndex++;
    }

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

        // Get user's messages in this channel
        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.ChannelId == channel.Id && m.CreatorId == UserId)
            .OrderBy(m => m.MessageId)
            .Take(MessageBatchSize)
            .Select(m => new { m.MessageId, m.Text, m.Entities, m.CreatedAt })
            .ToListAsync();

        if (messages.Count > 0)
        {
            var data = new
            {
                SpaceId = spaceId,
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                Messages = messages
            };

            await UploadJsonAsync(exportId, $"channels/{spaceId}/{channel.Id}.json", data);
        }

        cursor.ChannelIndex++;
    }

    #endregion

    #region Assembly

    private async Task ProcessAssemblyAsync()
    {
        var exportId = state.State.CurrentExportId!.Value;
        var prefix   = GetIntermediatePrefix(exportId);

        using var activity = UserDataExportInstrument.ActivitySource.StartActivity("Export.Assembly");
        activity?.SetTag("export.user_id", UserId.ToString());
        activity?.SetTag("export.export_id", exportId.ToString());

        // List all intermediate files
        var keys = await exportS3.ListObjectsAsync(prefix);
        if (keys.Count == 0)
        {
            logger.LogError("No intermediate data found for user {UserId}, export {ExportId}", UserId, exportId);
            state.State.Status        = ExportStatus.Failed;
            state.State.FailureReason = "No data collected";
            _processTimer?.Dispose();
            _processTimer = null;
            await state.WriteStateAsync();
            await NotifyPumpUnregisteredAsync();
            UserDataExportInstrument.ExportsFailed.Add(1,
                new KeyValuePair<string, object?>("reason", "no_data"));
            return;
        }

        logger.LogInformation("Assembling archive for user {UserId}, export {ExportId}: {FileCount} intermediate files",
            UserId, exportId, keys.Count);

        // Create zip archive in memory
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var key in keys)
            {
                var relativePath = key[prefix.Length..];
                var objectStream = await exportS3.GetObjectStreamAsync(key);
                if (objectStream == null) continue;

                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await objectStream.CopyToAsync(entryStream);
                await objectStream.DisposeAsync();
            }
        }

        zipStream.Position = 0;
        var archiveSize = zipStream.Length;

        // Upload final archive
        var archiveKey = $"exports/{UserId}/{exportId}/export-{DateTime.UtcNow:yyyy-MM-dd}.zip";
        var uploaded = await exportS3.PutObjectAsync(archiveKey, zipStream, "application/zip");

        if (!uploaded)
        {
            logger.LogError("Failed to upload archive for user {UserId}, export {ExportId}", UserId, exportId);
            state.State.Status        = ExportStatus.Failed;
            state.State.FailureReason = "Failed to upload archive";
            _processTimer?.Dispose();
            _processTimer = null;
            await state.WriteStateAsync();
            await NotifyPumpUnregisteredAsync();
            UserDataExportInstrument.ExportsFailed.Add(1,
                new KeyValuePair<string, object?>("reason", "upload_failed"));
            return;
        }

        // Clean up intermediate files
        await exportS3.DeletePrefixAsync(prefix);

        // Generate presigned download URL (48h)
        var downloadUrl = exportS3.GeneratePresignedGetUrl(archiveKey, (int)ArchiveTtl.TotalSeconds);

        // Update state
        state.State.Status       = ExportStatus.Completed;
        state.State.CompletedAt  = DateTimeOffset.UtcNow;
        state.State.LastExportCompletedAt = DateTimeOffset.UtcNow;
        state.State.ArchiveS3Key = archiveKey;
        state.State.DownloadUrl  = downloadUrl;

        _processTimer?.Dispose();
        _processTimer = null;
        await state.WriteStateAsync();
        await NotifyPumpUnregisteredAsync();

        // Record metrics
        var totalDuration = (DateTimeOffset.UtcNow - state.State.StartedAt!.Value).TotalSeconds;
        UserDataExportInstrument.ExportsCompleted.Add(1);
        UserDataExportInstrument.ExportDuration.Record(totalDuration);
        UserDataExportInstrument.ExportArchiveSizeBytes.Record(archiveSize);

        logger.LogInformation(
            "Export completed for user {UserId}, export {ExportId}. Archive size: {ArchiveSize} bytes, duration: {Duration:F1}s",
            UserId, exportId, archiveSize, totalDuration);

        // Send "export ready" email
        await SendExportReadyEmailAsync(downloadUrl);
    }

    #endregion

    #region Helpers

    private string GetIntermediatePrefix(Guid exportId)
        => $"exports/{UserId}/{exportId}/intermediate/";

    private async Task UploadJsonAsync(Guid exportId, string relativePath, object data)
    {
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        var key  = $"{GetIntermediatePrefix(exportId)}{relativePath}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await exportS3.PutObjectAsync(key, stream, "application/json");
    }

    private void CheckExpiration()
    {
        if (state.State.Status == ExportStatus.Completed
            && state.State.CompletedAt is { } completed
            && DateTimeOffset.UtcNow - completed > ArchiveTtl)
        {
            state.State.Status       = ExportStatus.Expired;
            state.State.DownloadUrl  = null;
            state.State.ArchiveS3Key = null;
        }
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
