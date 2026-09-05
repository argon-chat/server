namespace Argon.Grains;

using Argon.Api.Grains.Interfaces;
using Argon.Core.Features.Logic;
using Argon.Core.Features.Transport;
using Argon.Features.Storage;
using Argon.Features.Moderation;
using Features.Logic;
using ion.runtime;
using Orleans;
using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class UserGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IUserPresenceService presenceService,
    ILogger<IUserGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    IOptions<ClientAppsOptions> clientApps,
    AppHubServer appHubServer) : Grain, IUserGrain
{
    private static readonly TimeSpan DisplayNameCooldown = TimeSpan.FromMinutes(10);

    public async Task<Either<UpdateProfileResult, UpdateMeError>> UpdateProfileAsync(UserEditInput input, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        var userId = this.GetUserId();

        var user = await ctx.Users.FirstAsync(x => x.Id == userId, ct);
        var profile = await ctx.UserProfiles.FirstAsync(x => x.UserId == userId, ct);

        // Check if any premium-only field is being set
        var hasPremiumField = input.backgroundId.HasValue
                           || input.voiceCardEffectId.HasValue
                           || input.avatarFrameId.HasValue
                           || input.nickEffectId.HasValue
                           || input.primaryColor.HasValue
                           || input.accentColor.HasValue
                           || input.customStatus is not null;

        if (hasPremiumField && !user.HasActiveUltima)
            return UpdateMeError.PREMIUM_REQUIRED;

        // Validate preset IDs
        if (!ProfilePresetValidator.IsValidPresetId(input.backgroundId, input.voiceCardEffectId, input.avatarFrameId, input.nickEffectId))
            return UpdateMeError.INVALID_PRESET_ID;

        // DisplayName update with cooldown
        if (!string.IsNullOrEmpty(input.displayName))
        {
            var trimmed = input.displayName.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return UpdateMeError.DISPLAY_NAME_EMPTY;
            if (trimmed.Length > 32)
                return UpdateMeError.DISPLAY_NAME_TOO_LONG;

            if (user.DisplayNameChangedAt.HasValue &&
                DateTimeOffset.UtcNow - user.DisplayNameChangedAt.Value < DisplayNameCooldown)
                return UpdateMeError.COOLDOWN_ACTIVE;

            user.DisplayName = trimmed;
            user.DisplayNameChangedAt = DateTimeOffset.UtcNow;
        }

        // Avatar update
        if (!string.IsNullOrEmpty(input.avatarId))
            user.AvatarFileId = input.avatarId;

        // Premium profile fields
        if (input.backgroundId.HasValue)
            profile.BackgroundId = input.backgroundId.Value;
        if (input.voiceCardEffectId.HasValue)
            profile.VoiceCardEffectId = input.voiceCardEffectId.Value;
        if (input.avatarFrameId.HasValue)
            profile.AvatarFrameId = input.avatarFrameId.Value;
        if (input.nickEffectId.HasValue)
            profile.NickEffectId = input.nickEffectId.Value;
        if (input.primaryColor.HasValue)
            profile.PrimaryColor = input.primaryColor.Value;
        if (input.accentColor.HasValue)
            profile.AccentColor = input.accentColor.Value;
        if (input.customStatus is not null)
            profile.CustomStatus = input.customStatus.Length > 128 ? input.customStatus[..128] : input.customStatus;
        if (input.customStatusIconId is not null)
            profile.CustomStatusIconId = input.customStatusIconId;

        // Bio is truncated nowhere: the column caps at 512 and silently cutting somebody's "about me"
        // mid-sentence is worse than telling them it did not fit.
        if (input.bio is not null)
        {
            if (input.bio.Length > 512)
                return UpdateMeError.BIO_TOO_LONG;

            // An empty string is how the client says "clear it"; storing "" instead of null would
            // make an emptied bio read back as present-but-blank.
            profile.Bio = input.bio.Length == 0 ? null : input.bio;
        }

        ctx.Users.Update(user);
        ctx.UserProfiles.Update(profile);
        await ctx.SaveChangesAsync(ct);

        var userDto = UserEntity.Map(user);
        var profileDto = UserProfileEntity.Map(profile);

        // Broadcast to all spaces
        var userServers = await GetMyServersIds(ct);
        await BroadcastToSpacesAsync(userServers, userDto, userId, profileDto, ct);

        return new UpdateProfileResult(userDto, profileDto);
    }

    public async ValueTask ResetPremiumProfileAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        var userId = this.GetPrimaryKey();

        var user = await ctx.Users.AsNoTracking().FirstAsync(x => x.Id == userId, ct);
        var profile = await ctx.UserProfiles.AsNoTracking().FirstAsync(x => x.UserId == userId, ct);

        var userDto = UserEntity.Map(user);
        var profileDto = UserProfileEntity.Map(profile);

        var userServers = await GetMyServersIds(ct);
        await BroadcastToSpacesAsync(userServers, userDto, userId, profileDto, ct);
    }

    private async Task BroadcastToSpacesAsync(List<Guid> spaceIds, ArgonUser userDto, Guid userId, ArgonUserProfile profileDto, CancellationToken ct = default)
    {
        foreach (var spaceId in spaceIds)
        {
            await appHubServer.BroadcastSpace(new UserUpdated(spaceId, userDto), spaceId, ct);
            await appHubServer.BroadcastSpace(new UserProfileUpdated(spaceId, userId, profileDto), spaceId, ct);
        }
    }

    public async Task<UserEntity> GetMe()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .AsNoTracking()
           .FirstAsync(user => user.Id == this.GetPrimaryKey());
    }

    public async Task<LegalState> GetLegalState()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users
           .AsNoTracking()
           .FirstAsync(u => u.Id == this.GetPrimaryKey());

        return new LegalState(user.AgreeTosVersion, user.AgreePrivacyVersion);
    }

    public async Task<LegalState> AcceptLegal(string tosVersion, string privacyVersion)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstAsync(u => u.Id == this.GetPrimaryKey());
        user.AgreeTosVersion     = tosVersion;
        user.AgreePrivacyVersion = privacyVersion;
        await ctx.SaveChangesAsync();

        return new LegalState(user.AgreeTosVersion, user.AgreePrivacyVersion);
    }

    public async Task<ArgonUser> GetAsArgonUser()
    {
        await using var ctx = await context.CreateDbContextAsync();

        // Only IsVerified is needed from the bot; projecting it avoids materializing the
        // whole TPT BotEntity (DevApps + Bots) on this hot path.
        var row = await ctx.Users
           .AsNoTracking()
           .Where(u => u.Id == this.GetPrimaryKey())
           .Select(u => new { User = u, IsVerified = u.BotEntity != null && u.BotEntity.IsVerified })
           .FirstAsync();

        return UserEntity.Map(row.User, row.IsVerified);
    }

    public async Task<ArgonUserProfile> GetMyProfile()
    {
        await using var ctx = await context.CreateDbContextAsync();
        var profile = await ctx.UserProfiles
           .AsNoTracking()
           .FirstAsync(x => x.UserId == this.GetPrimaryKey());

        return profile.ToDto();
    }

    public async Task<List<ArgonSpaceBase>> GetMyServers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var result = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Include(x => x.Space)
           .Where(x => x.UserId == this.GetPrimaryKey())
           .Select(x => x.Space)
           .ToListAsync();

        return result.Select(x => new ArgonSpaceBase(x.Id, x.Name, x.Description!, x.AvatarFileId, x.TopBannedFileId,
            x.BoostCount, x.BoostLevel, x.IsVerified, x.IsOfficial, x.HideBoostStrip, x.InviteImageFileId)).ToList();
    }

    public async Task<List<Guid>> GetMyServersIds(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        return await ctx.Users
           .AsNoTracking()
           .Include(user => user.ServerMembers)
           .Where(u => u.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.SpaceId)
           .ToListAsync(cancellationToken: ct);
    }

    public async ValueTask BroadcastPresenceAsync(UserActivityPresence presence, string sessionId)
    {
        var userId = this.GetPrimaryKey();
        // Store this session's activity (per-session, so other devices aren't clobbered), then broadcast
        // the representative activity across all the user's sessions. The wire still carries one activity
        // ("last"); the full per-session set lives server-side for when the contract grows.
        await presenceService.BroadcastActivityPresence(presence, userId, sessionId);
        var representative = await presenceService.GetUsersActivityPresence(userId) ?? presence;

        var servers = await GetMyServersIds();
        await Task.WhenAll(servers.Select(server =>
            GrainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserPresence(userId, representative)));
    }

    public async ValueTask RemoveBroadcastPresenceAsync(string sessionId, bool alwaysBroadcast)
    {
        var userId      = this.GetPrimaryKey();
        var hadActivity = await presenceService.RemoveActivityPresence(userId, sessionId);

        // Skip the fan-out only on the session-ended path when this session had no activity. The
        // explicit user-cleared path (alwaysBroadcast) must still broadcast even if the key already
        // lapsed by TTL — otherwise observers keep showing a stale activity indefinitely.
        if (!hadActivity && !alwaysBroadcast)
            return;

        logger.LogInformation("Clearing activity presence for {userId} session {sessionId} (hadActivity={hadActivity})",
            userId, sessionId, hadActivity);

        // Another device may still have an activity — fall back to it; otherwise clear.
        var representative = await presenceService.GetUsersActivityPresence(userId);
        var servers        = await GetMyServersIds();
        await Task.WhenAll(servers.Select(server =>
            representative is not null
                ? GrainFactory.GetGrain<ISpaceGrain>(server).SetUserPresence(userId, representative)
                : GrainFactory.GetGrain<ISpaceGrain>(server).RemoveUserPresence(userId)));
    }

    //public async ValueTask CreateSocialBound(SocialKind kind, string userData, string socialId)
    //{
    //    await using var ctx = await context.CreateDbContextAsync();

    //    await ctx.SocialIntegrations.AddAsync(new UserSocialIntegration()
    //    {
    //        Kind     = kind,
    //        SocialId = socialId,
    //        UserData = userData,
    //        Id       = ArgonId.New(),
    //        UserId   = this.GetPrimaryKey()
    //    });
    //    await ctx.SaveChangesAsync();
    //}

    //public async ValueTask<List<UserSocialIntegrationDto>> GetMeSocials()
    //{
    //    await using var ctx = await context.CreateDbContextAsync();
    //    return await ctx.SocialIntegrations.AsNoTracking().Where(x => x.UserId == this.GetPrimaryKey()).ToListAsync().ToDto();
    //}

    //public async ValueTask<bool> DeleteSocialBoundAsync(string kind, Guid socialId)
    //{
    //    await using var ctx = await context.CreateDbContextAsync();

    //    try
    //    {
    //        var result = await ctx.SocialIntegrations.Where(x => x.Id == socialId).ExecuteDeleteAsync();
    //        return result == 1;
    //    }
    //    catch (Exception e)
    //    {
    //        logger.LogError(e, "failed delete social bound by {socialId}", socialId);
    //        return false;
    //    }
    //}

    //[OneWay]
    public async ValueTask UpgradePasswordDigest(string digest)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == this.GetPrimaryKey());

        if (user is null)
            return;

        user.PasswordDigest = digest;
        await ctx.SaveChangesAsync();

        logger.LogInformation("Upgraded the password digest for {UserId}", this.GetPrimaryKey());
    }

    /// <summary>
    /// Records that this user signed in, or connected, from the calling machine.
    /// </summary>
    /// <remarks>
    /// Everything about the machine comes out of the request context the Ion layer set: address,
    /// country, the application id and the client's description of itself. Callers that reach this
    /// without those — a hub connection has ids and nothing else — would write a row that says
    /// "unknown" in every column, which is why the hub no longer calls it.
    /// </remarks>
    public async ValueTask UpdateUserDeviceHistory()
    {
        await using var ctx = await context.CreateDbContextAsync();

        try
        {
            var userId     = this.GetPrimaryKey();
            var machineId  = this.GetUserMachineId();
            var client     = this.GetUserClient();
            var appId      = this.GetUserAppId();
            var deviceType = ClientIdentity.DeviceType(clientApps.Value.Find(appId, client), client);
            var region     = this.GetUserRegion() is { Length: > 0 } country && country != GeoLocation.UnknownCountry ? country : "unknown";
            var ip         = this.GetUserIp() ?? "unknown";
            var now        = DateTimeOffset.UtcNow;

            logger.LogDebug("Device history for {UserId}: machine={MachineId} app={AppId} type={DeviceType} region={Region}",
                userId, machineId, appId, deviceType, region);

            var history = await ctx.DeviceHistories.FirstOrDefaultAsync(x => x.UserId == userId && x.MachineId == machineId);

            if (history is not null)
            {
                history.LastKnownIP   = ip;
                history.RegionAddress = region;
                history.LastLoginTime = now;

                // Rows written before the application was known say "unknown" and carry a guessed
                // type; a connection that does know overwrites both, and one that does not leaves
                // whatever was there rather than degrading it.
                if (!string.IsNullOrWhiteSpace(appId))
                    history.AppId = appId;
                if (deviceType != DeviceTypeKind.Unknown)
                    history.DeviceType = deviceType;

                ctx.Update(history);
            }
            else
            {
                await ctx.DeviceHistories.AddAsync(new UserDeviceHistoryEntity
                {
                    AppId         = string.IsNullOrWhiteSpace(appId) ? "unknown" : appId,
                    DeviceType    = deviceType,
                    LastKnownIP   = ip,
                    LastLoginTime = now,
                    MachineId     = machineId,
                    RegionAddress = region,
                    UserId        = userId
                });
            }

            await ctx.SaveChangesAsync();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed update user device history");
        }
    }

    public async ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadUserFile(UserFileKind kind, CancellationToken ct = default)
    {
        try
        {
            var userId = this.GetPrimaryKey();
            var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(userId);
            var purpose = kind switch
            {
                UserFileKind.Avatar => FilePurpose.Avatar,
                _                   => FilePurpose.Avatar
            };
            var response = await fileGrain.RequestUploadAsync(
                new FileUploadRequest(purpose, "image/", 0, null, null), ct);

            return new UploadTicket(response.BlobId, response.Url, response.Fields, response.TtlSeconds);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed upload user file {kind}", kind);
            return UploadFileError.INTERNAL_ERROR;
        }
    }

    public async ValueTask CompleteUploadUserFile(Guid blobId, UserFileKind kind, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();
        var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(userId);
        var fileInfo = await fileGrain.FinalizeUploadAsync(blobId, ct);

        if (kind == UserFileKind.Avatar)
        {
            var modGrain = GrainFactory.GetGrain<IContentModerationGrain>(Guid.Empty);
            var modResult = await modGrain.EvaluateAsync(fileInfo.S3Key, FilePurpose.Avatar, ct);

            if (modResult.Action == ContentAction.Deny)
            {
                await fileGrain.DecrementRefAsync(fileInfo.FileId, ct);

                await RecordViolationAsync(userId, fileInfo.FileId, FilePurpose.Avatar, modResult, ct);

                logger.LogWarning(
                    "Avatar upload rejected for user {UserId}, file {FileId}, stages={Stages}, scores={Scores}, refined={RefinedScores}",
                    userId, fileInfo.FileId, modResult.StagesUsed,
                    FormatScores(modResult.Scores), FormatScores(modResult.RefinedScores));

                throw new ContentViolationException("Avatar rejected by content moderation");
            }
        }

        await UpdateFileIdFor(kind, fileInfo.FileId, fileInfo.S3Key, ct);
    }

    public async ValueTask<LockedAuthStatus> GetLimitationForUser()
    {
        var user = await GetMe();

        // A lockdown with an expiry that has passed is no lockdown. Nothing sweeps the columns
        // clear, so this is where a timed ban actually ends — the request interceptor reads the
        // same two fields the same way.
        if (user.LockdownReason is LockdownReason.NONE
         || user.LockDownExpiration is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return new LockedAuthStatus(null, null, false, LockdownSeverity.Low);

        return new LockedAuthStatus(user.LockdownReason, user.LockDownExpiration?.UtcDateTime ?? DateTime.Now.AddYears(20),
            user.LockDownIsAppealable, DetermineSeverity(user.LockdownReason));

        LockdownSeverity DetermineSeverity(LockdownReason reason)
            => reason switch
            {
                LockdownReason.NONE                => LockdownSeverity.Low,
                LockdownReason.UNDER_INVESTIGATION => LockdownSeverity.Middle,
                LockdownReason.INCITING_MOMENT     => LockdownSeverity.Middle,
                _                                  => LockdownSeverity.Critical
            };
    }

    private ValueTask UpdateFileIdFor(UserFileKind kind, Guid fileId, string s3Key, CancellationToken ct = default)
        => kind switch
        {
            UserFileKind.Avatar => UpdateAvatarFileId(fileId, s3Key, ct),
            _                   => throw new NotImplementedException()
        };

    private async ValueTask UpdateAvatarFileId(Guid fileId, string s3Key, CancellationToken ct = default)
    {
        await using var ctx    = await context.CreateDbContextAsync(ct);
        var             userId = this.GetPrimaryKey();

        var user = await ctx.Users.FirstAsync(x => x.Id == userId, cancellationToken: ct);

        var currentAvatarId = user.AvatarFileId;

        // Store S3 key as avatar ID (with FlatAvatarKeys this is just the fileId GUID string)
        user.AvatarFileId = s3Key;

        await ctx.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(currentAvatarId))
        {
            try
            {
                // For flat keys the stored value IS the fileId; for nested keys extract last segment
                var oldFileIdStr = currentAvatarId.Contains('/') ? currentAvatarId.Split('/')[^1] : currentAvatarId;
                if (Guid.TryParse(oldFileIdStr, out var oldFileId))
                    await GrainFactory.GetGrain<IFileStorageGrain>(userId).DecrementRefAsync(oldFileId, ct);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed decrement fileId");
            }
        }

        var userServers = await GetMyServersIds(ct);
        var userDto = UserEntity.Map(user);

        foreach (var spaceId in userServers)
            await appHubServer.BroadcastSpace(new UserUpdated(spaceId, userDto), spaceId, ct);
    }

    public async ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();
        var aggregatedStatus = await presenceService.GetAggregatedStatusAsync(userId, ct);

        logger.LogDebug("Aggregated status for user {userId}: {status}", userId, aggregatedStatus);

        // Hysteresis: only fan out when the aggregate actually changed since our last broadcast.
        // Connects/heartbeats/transient reconnects that re-compute the same status now cost nothing
        // (no per-space SetUserStatus, no replay-stream append). All status broadcast paths funnel
        // through here so the last-broadcast record stays consistent.
        if (!await presenceService.MarkBroadcastIfChangedAsync(userId, aggregatedStatus, ct))
            return;

        var servers = await GetMyServersIds(ct);
        await Task.WhenAll(servers.Select(server =>
            GrainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserStatus(userId, aggregatedStatus)));

        await BroadcastStatusToFriendsAsync(userId, aggregatedStatus, ct);
    }

    /// <summary>
    /// The mirror of the fan-out below: a session that has just connected has missed every status
    /// event that fired before it existed, so friends who were already online would read as offline
    /// until they next changed anything.
    /// </summary>
    /// <remarks>
    /// One friend-id query and one batched presence read per session start. Only friends who are
    /// actually online are sent - the client's own default for an unknown user is offline.
    /// </remarks>
    public async ValueTask PushFriendPresenceAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var friendIds = await ctx.Friends
           .AsNoTracking()
           .Where(x => x.UserId == userId)
           .Select(x => x.FriendId)
           .ToListAsync(ct);

        if (friendIds.Count == 0)
            return;

        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId, ct);
        if (sessions.Count == 0)
            return;

        var statuses = await presenceService.BatchGetAggregatedStatusAsync(friendIds, ct);

        foreach (var (friendId, status) in statuses)
        {
            if (status == UserStatus.Offline)
                continue;

            await notifier.NotifySessionsAsync(
                sessions,
                new UserChangedStatus(Guid.Empty, friendId, status, new IonArray<string>([""])),
                ct);
        }
    }

    /// <summary>
    /// UserChangedStatus is only ever fired by SpaceGrain, to the members of that space - so a
    /// friend you share no space with never learned that you came online, and their friends list
    /// sat on whatever it last happened to cache (for someone just added: offline, forever).
    /// </summary>
    /// <remarks>
    /// Only reached when the aggregate actually changed - the hysteresis check above already
    /// swallowed heartbeats and reconnects - so this costs one friend-id query and one notify per
    /// real transition. A friend who is also a space member receives the event twice; deduplicating
    /// would cost a membership join on every transition, and the client keys the update on the user
    /// id alone, so the second one is a no-op.
    /// </remarks>
    private async Task BroadcastStatusToFriendsAsync(Guid userId, UserStatus status, CancellationToken ct)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var friendIds = await ctx.Friends
           .AsNoTracking()
           .Where(x => x.UserId == userId)
           .Select(x => x.FriendId)
           .ToListAsync(ct);

        if (friendIds.Count == 0)
            return;

        var sessionsPerFriend = await Task.WhenAll(
            friendIds.Select(friendId => sessionDiscovery.GetUserSessionsAsync(friendId, ct)));

        var sessions = sessionsPerFriend.SelectMany(x => x).ToList();
        if (sessions.Count == 0)
            return;

        // There is no space this is about; the client reads userId and status and ignores the rest.
        await notifier.NotifySessionsAsync(
            sessions,
            new UserChangedStatus(Guid.Empty, userId, status, new IonArray<string>([""])),
            ct);
    }

    private async ValueTask RecordViolationAsync(
        Guid userId, Guid fileId, FilePurpose purpose,
        ContentModerationResult result, CancellationToken ct)
    {
        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);
            ctx.ContentViolations.Add(new ContentViolationEntity
            {
                Id = ArgonId.New(),
                UserId = userId,
                FileId = fileId,
                FilePurpose = purpose,
                StagesUsed = result.StagesUsed,
                PrimaryScores = result.Scores,
                RefinedScores = result.RefinedScores,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync(ct);

            ModerationInstruments.ViolationsRecorded.Add(1,
                new KeyValuePair<string, object?>("purpose", purpose.ToString()));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to record content violation for user {UserId}", userId);
        }
    }

    private static string FormatScores(Dictionary<string, float>? scores)
        => scores is null or { Count: 0 }
            ? "-"
            : string.Join(", ", scores.Select(kv => $"{kv.Key}={kv.Value:P1}"));
}