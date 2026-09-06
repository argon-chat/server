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

    /// <inheritdoc cref="IUserGrain.ResolveChannelSpaceIfMemberAsync"/>
    public async Task<Guid?> ResolveChannelSpaceIfMemberAsync(Guid channelId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var userId = this.GetPrimaryKey();

        // One query, not two, and not three: an unknown channel, a channel in somebody else's space
        // and a channel this member may not view are the same answer to the caller, and asking
        // separately would leak which of the three it was. Everything the entitlement evaluator
        // reads is pulled in the same round trip — the member's archetypes with what each grants,
        // and the channel's overwrites.
        var resolved = await ctx.Channels
           .AsNoTracking()
           .Where(c => c.Id == channelId)
           .Select(c => new
            {
                c.SpaceId,
                Overwrites = c.EntitlementOverwrites
                   .Select(o => new { o.Scope, o.ArchetypeId, o.SpaceMemberId, o.Allow, o.Deny })
                   .ToList(),
                Member = c.Space!.Users
                   .Where(m => m.UserId == userId)
                   .Select(m => new
                    {
                        m.Id,
                        Archetypes = m.SpaceMemberArchetypes
                           .Select(a => new { a.ArchetypeId, a.Archetype.Entitlement })
                           .ToList()
                    })
                   .FirstOrDefault()
            })
           .FirstOrDefaultAsync(ct);

        if (resolved?.Member is not { } member)
            return null;

        // Rebuilt into the two shapes the evaluator reads, exactly as SpaceReadGrain does from its
        // cached projections — the alternative is loading two EF graphs whose navigations are cyclic.
        var asMember = new SpaceMemberEntity
        {
            Id                    = member.Id,
            SpaceMemberArchetypes = member.Archetypes
               .Select(a => new SpaceMemberArchetypeEntity { ArchetypeId = a.ArchetypeId })
               .ToList()
        };

        var asChannel = new ChannelEntity
        {
            EntitlementOverwrites = resolved.Overwrites
               .Select(o => new ChannelEntitlementOverwriteEntity
                {
                    Scope         = o.Scope,
                    ArchetypeId   = o.ArchetypeId,
                    SpaceMemberId = o.SpaceMemberId,
                    Allow         = o.Allow,
                    Deny          = o.Deny
                })
               .ToList()
        };

        var basePermissions = member.Archetypes
           .Aggregate(ArgonEntitlement.None, (permissions, a) => permissions | a.Entitlement);

        // Deliberately the same two calls, in the same order, as SpaceReadGrain.VisibleChannelsAsync:
        // the hub gate and the channel list a client is shown have to answer alike, or a channel is
        // either invisible-but-subscribable or visible-but-refused. Administrator passes regardless —
        // IsEntitlementSatisfied checks the flag first — which is why an owner who is also in the
        // denied archetype still gets in.
        if (!EntitlementAnalyzer.IsEntitlementSatisfied(
                EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, asMember, asChannel),
                ArgonEntitlement.ViewChannel))
            return null;

        return resolved.SpaceId;
    }

    /// <summary>
    /// How many spaces are asked for their voice slot at once.
    /// </summary>
    /// <remarks>
    /// The lookups are independent and each one may cold-activate a <c>SpaceGrain</c>, so a user in
    /// eighty spaces used to pay eighty sequential grain hops on the offline path — and a rolling
    /// deploy fires tens of thousands of those within a couple of minutes. Bounded rather than
    /// unbounded because the fan-out is per user and the storm is the whole problem: eighty parallel
    /// activations each is the same stampede with less waiting.
    /// </remarks>
    private const int VoiceSlotLookupConcurrency = 8;

    /// <inheritdoc cref="IUserGrain.LeaveAllVoiceAsync"/>
    public async ValueTask LeaveAllVoiceAsync(CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        List<Guid> spaceIds;

        try
        {
            // Inside the guard. This sat outside every try, so one database hiccup threw out of here,
            // out of UserSessionGrain.FinalizeOfflineAsync, and past SelfDestroy and the session-end
            // instrumentation — with the grace reminder already unregistered, so nothing retried.
            spaceIds = await GetMyServersIds(ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not list the spaces of user {userId} to take them out of voice", userId);
            return;
        }

        var slots = new List<(Guid SpaceId, VoiceSlot Slot)>();

        foreach (var chunk in spaceIds.Chunk(VoiceSlotLookupConcurrency))
        {
            var found = await Task.WhenAll(chunk.Select(async spaceId =>
            {
                try
                {
                    return (SpaceId: spaceId, Slot: await GrainFactory.GetGrain<ISpaceGrain>(spaceId).GetUserVoiceSlotAsync(userId));
                }
                catch (Exception e)
                {
                    // One unreachable space must not strand the user in the calls held by the others.
                    logger.LogWarning(e, "Could not read the voice slot of user {userId} in space {spaceId}", userId, spaceId);
                    return (SpaceId: spaceId, Slot: null);
                }
            }));

            slots.AddRange(found.Where(x => x.Slot is not null).Select(x => (x.SpaceId, x.Slot!)));
        }

        foreach (var (spaceId, slot) in slots)
        {
            try
            {
                // Re-read immediately before the hang-up, not once at the top of FinalizeOfflineAsync.
                // Between that read and here lie a database query and a grain hop per space — easily
                // hundreds of milliseconds — and the user may have picked another device up inside it.
                // Hanging up a call somebody is actively in is worse than leaving a stale occupant,
                // and the stale occupant is cleaned up by the next offline anyway.
                if (await presenceService.IsUserOnlineAsync(userId, ct))
                {
                    logger.LogInformation("Leaving user {userId} in voice: a session came back online while the offline path was running", userId);
                    return;
                }

                logger.LogInformation("Taking user {userId} out of voice channel {channelId} in space {spaceId}: no live session left",
                    userId, slot.ChannelId, spaceId);

                await GrainFactory.GetGrain<IChannelGrain>(slot.ChannelId).Leave(userId);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not take user {userId} out of voice in space {spaceId}", userId, spaceId);
            }
        }
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

    public ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default)
        => AggregateAndBroadcastStatusAsync([], ct);

    public async ValueTask AggregateAndBroadcastStatusAsync(Guid[] seedSpaces, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();
        var aggregatedStatus = await presenceService.GetAggregatedStatusAsync(userId, ct);

        logger.LogDebug("Aggregated status for user {userId}: {status}", userId, aggregatedStatus);

        // A seed with nothing to seed. Offline is announced as silence on a join — the space has never
        // heard of this member, so there is no stale value to correct — and the hysteresis record is
        // deliberately left alone: writing Offline into it here is what raced the fan-out below when
        // this read lived in SpaceGrain.UserJoined.
        if (seedSpaces.Length > 0 && aggregatedStatus is UserStatus.Offline)
            return;

        // Hysteresis: only fan out when the aggregate actually changed since our last broadcast.
        // Connects/heartbeats/transient reconnects that re-compute the same status now cost nothing
        // (no per-space SetUserStatus, no replay-stream append). All status broadcast paths funnel
        // through here so the last-broadcast record stays consistent.
        if (!await presenceService.MarkBroadcastIfChangedAsync(userId, aggregatedStatus, ct))
        {
            // Except for the seeds, which are not a transition at all: this space has been told
            // nothing about this member and cannot be caught up by a record saying everyone already
            // knows. Nothing else is announced and the record is not touched, so a join costs one
            // event in one space rather than a fan-out to every space the user is in.
            await SeedAsync(seedSpaces, userId, aggregatedStatus, ct);
            return;
        }

        // The seeds are announced below rather than here — they are the one part of this fan-out that
        // must not go through ISpaceGrain — so they come out of the grain call list.
        var servers = await GetMyServersIds(ct);

        await Task.WhenAll(servers
           .Where(server => !seedSpaces.Contains(server))
           .Select(server => GrainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserStatus(userId, aggregatedStatus)));

        await SeedAsync(seedSpaces, userId, aggregatedStatus, ct);

        await BroadcastStatusToFriendsAsync(userId, aggregatedStatus, ct);
    }

    /// <summary>Announces the status straight to each seed space's group.</summary>
    /// <remarks>
    /// <para><b>Not through <c>ISpaceGrain.SetUserStatus</c>, and it cannot be.</b> The only caller
    /// that passes seeds is <c>SpaceGrain.UserJoined</c>, which awaits this from inside its own turn —
    /// so a grain call back into that space is a cycle into a non-reentrant activation, and Orleans
    /// answers it with a thirty-second timeout and a failed join rather than with reentrancy (the
    /// join arrives through <c>InviteGrain.AcceptAsync</c>, and the call-chain reentrancy the request
    /// pipeline enables did not cover it).</para>
    ///
    /// <para>What it publishes is what <c>SetUserStatus</c> publishes — one <c>UserChangedStatus</c> to
    /// the space's group, through the same <c>AppHubServer</c> that grain's <c>Fire</c> uses — so the
    /// room cannot tell the difference. If <c>SetUserStatus</c> ever grows a second responsibility,
    /// this is the line that has to grow with it.</para>
    /// </remarks>
    private async Task SeedAsync(Guid[] seedSpaces, Guid userId, UserStatus status, CancellationToken ct)
    {
        foreach (var spaceId in seedSpaces.Distinct())
            await appHubServer.BroadcastSpace(
                new UserChangedStatus(spaceId, userId, status, new IonArray<string>([""])), spaceId, ct);
    }

    /// <summary>
    /// The mirror of the fan-out below: a session that has just connected has missed every status
    /// event that fired before it existed, so friends who were already online would read as offline
    /// until they next changed anything.
    /// </summary>
    /// <remarks>
    /// <para>One friend-id query and one batched presence read per session start. Only friends who
    /// are actually online are sent - the client's own default for an unknown user is offline.</para>
    ///
    /// <para>One event per friend is the wire contract — the client keys
    /// <c>UserChangedStatus</c> on the user id and has nowhere to put a batch — but the delivery is
    /// not per friend any more. This used to call <c>NotifySessionsAsync</c> once per online friend,
    /// and every one of those calls opened a service scope, resolved an <c>AppHubServer</c> and
    /// walked the same one-element list of distinct user ids: N scopes to address the same person N
    /// times. The addressee is settled once here, and the events go out together.</para>
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

        // Still asked for, and still a guard rather than an address: a user with no live session has
        // nothing to be caught up, and ForUser below would write a replay entry nobody asked for.
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId, ct);
        if (sessions.Count == 0)
            return;

        var statuses = await presenceService.BatchGetAggregatedStatusAsync(friendIds, ct);

        var online = statuses
           .Where(x => x.Value != UserStatus.Offline)
           .Select(x => x.Key)
           .ToList();

        // Bounded, because this runs on the connect path and the fan-out is per friend: a user with
        // three hundred online friends must not open three hundred concurrent publishes, nor wait
        // for three hundred sequential ones before the hub call returns.
        foreach (var chunk in online.Chunk(FriendFanOutConcurrency))
        {
            await Task.WhenAll(chunk.Select(async friendId =>
            {
                try
                {
                    await appHubServer.ForUser(
                        new UserChangedStatus(Guid.Empty, friendId, statuses[friendId], new IonArray<string>([""])),
                        userId, ct);
                }
                catch (Exception e)
                {
                    // One friend's status failing to publish must not cost the connecting client the
                    // rest of its roster; it reads as offline, which is what it read before this ran.
                    logger.LogWarning(e, "Could not push the status of friend {friendId} to user {userId}", friendId, userId);
                }
            }));
        }
    }

    /// <summary>
    /// How many friend-scoped publishes or session lookups are in flight at once.
    /// </summary>
    /// <remarks>
    /// Both friend fan-outs sit on paths a session grain is awaiting — a connect in one case, a
    /// status transition in the other — so neither may be sequential; and both are per user, so
    /// neither may be unbounded, or one very sociable account becomes a thundering herd of its own.
    /// </remarks>
    private const int FriendFanOutConcurrency = 16;

    /// <summary>
    /// UserChangedStatus is only ever fired by SpaceGrain, to the members of that space - so a
    /// friend you share no space with never learned that you came online, and their friends list
    /// sat on whatever it last happened to cache (for someone just added: offline, forever).
    /// </summary>
    /// <remarks>
    /// <para>Only reached when the aggregate actually changed - the hysteresis check above already
    /// swallowed heartbeats and reconnects - so this costs one friend-id query and one notify per
    /// real transition. A friend who is also a space member receives the event twice; deduplicating
    /// would cost a membership join on every transition, and the client keys the update on the user
    /// id alone, so the second one is a no-op.</para>
    ///
    /// <para>The session lookups are bounded rather than one <c>Task.WhenAll</c> over every friend:
    /// each one is a Redis round trip per session of that friend, this sits on the path a session
    /// grain awaits, and an account with a thousand friends should not open a thousand of them at
    /// once. Ordering per recipient is unaffected — the lookups only decide who to address, and the
    /// single notify below is what actually sends, one publish per distinct user.</para>
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

        var sessions = new List<UserSessionDescriptor>();

        foreach (var chunk in friendIds.Chunk(FriendFanOutConcurrency))
        {
            var perFriend = await Task.WhenAll(
                chunk.Select(friendId => sessionDiscovery.GetUserSessionsAsync(friendId, ct)));

            sessions.AddRange(perFriend.SelectMany(x => x));
        }

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