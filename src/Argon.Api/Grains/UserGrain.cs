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

        // Avatar update, and the ownership test is the point of it. Defect R1: this used to assign
        // whatever the client sent. The value is a file id (or the S3 key ending in one), so an
        // account could point its avatar at a file somebody else uploaded — which then made that
        // stranger's file part of this account's erasure, since AnonymizeUserAsync releases the
        // avatar by id. Refused here rather than only in FileStorageGrain because a foreign avatar is
        // a broken profile even when nothing releases it: the file's owner can delete it underneath.
        if (!string.IsNullOrEmpty(input.avatarId))
        {
            if (!await OwnsFileAsync(ctx, userId, input.avatarId, ct))
                return UpdateMeError.INVALID_PRESET_ID;

            user.AvatarFileId = input.avatarId;
        }

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

    /// <summary>
    /// Whether the avatar id a profile edit is asking for names a finalised file this account owns.
    /// </summary>
    /// <remarks>
    /// <para>The stored value is an S3 key, and with <c>StorageOptions.FlatAvatarKeys</c> the key is
    /// the bare file id — so the id is the last path segment either way, which is exactly how
    /// <see cref="UpdateAvatarFileId"/> reads the previous value back when it releases it.</para>
    ///
    /// <para><c>Finalized</c> is required because an unfinalised row is a signed upload URL nobody has
    /// used yet: it has no reference counter, no bytes behind it, and setting it as an avatar would
    /// leave the account pointing at a key the store has never heard of. <c>IgnoreQueryFilters</c> for
    /// the same reason <c>FileStorageGrain</c> uses it — ownership is a fact about the row, not about
    /// whether something has soft-deleted it.</para>
    ///
    /// <para>The refusal is reported as <c>INVALID_PRESET_ID</c>, the enum's existing "an id in this
    /// input does not name something you may use". A member of its own would be the honest answer and
    /// costs a schema change to <c>UserInteraction.ion</c> and every client generated from it; the
    /// contract this fix owes is that a foreign avatar id is <em>refused</em>, and it is.</para>
    /// </remarks>
    private static async Task<bool> OwnsFileAsync(ApplicationDbContext ctx, Guid userId, string avatarId, CancellationToken ct)
    {
        var lastSegment = avatarId.Contains('/') ? avatarId.Split('/')[^1] : avatarId;

        if (!Guid.TryParse(lastSegment, out var fileId))
            return false;

        return await ctx.Files
           .IgnoreQueryFilters()
           .AsNoTracking()
           .AnyAsync(f => f.Id == fileId && f.OwnerId == userId && f.Finalized, ct);
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

    /// <inheritdoc cref="IUserGrain.GetIdentityIncludingDeleted"/>
    /// <remarks>
    /// <para>The same projection as <see cref="GetAsArgonUser"/> — only <c>IsVerified</c> is needed
    /// off the bot, so the TPT join is never materialised — with two deliberate differences, and both
    /// of them are the point of the method (defect ACC-06, pinned by
    /// <c>AccountPeripheralTests.A_deleted_peer_still_resolves_to_the_tombstone_identity</c>).</para>
    ///
    /// <para><c>IgnoreQueryFilters</c>, so the global <c>!IsDeleted</c> filter does not hide an account
    /// that deletion anonymised in place; and <c>FirstOrDefaultAsync</c>, so an id nobody ever had is
    /// an answer rather than an <c>InvalidOperationException</c> travelling out of an RPC as
    /// <c>UPSTREAM_ERROR</c>. What comes back for a deleted account is the tombstone
    /// <c>AccountDeletionGrain.AnonymizeUserAsync</c> wrote, carrying <c>UserFlag.DELETED</c> — the
    /// same thing <c>SpaceGrain.PrefetchUser</c> answers for that account, which is what the two
    /// surfaces disagreeing about was.</para>
    /// </remarks>
    public async Task<ArgonUser?> GetIdentityIncludingDeleted()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var row = await ctx.Users
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(u => u.Id == this.GetPrimaryKey())
           .Select(u => new { User = u, IsVerified = u.BotEntity != null && u.BotEntity.IsVerified })
           .FirstOrDefaultAsync();

        return row is null ? null : UserEntity.Map(row.User, row.IsVerified);
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

    /// <inheritdoc cref="IUserGrain.BroadcastPresenceAsync"/>
    public ValueTask BroadcastPresenceAsync(UserActivityPresence presence, string sessionId)
        => GrainFactory.GetGrain<IUserPresenceGrain>(this.GetPrimaryKey()).BroadcastPresenceAsync(presence, sessionId);

    /// <inheritdoc cref="IUserGrain.RemoveBroadcastPresenceAsync"/>
    public ValueTask RemoveBroadcastPresenceAsync(string sessionId, bool alwaysBroadcast)
        => GrainFactory.GetGrain<IUserPresenceGrain>(this.GetPrimaryKey())
           .RemoveBroadcastPresenceAsync(sessionId, alwaysBroadcast);

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
        var userId     = this.GetPrimaryKey();
        var machineId  = this.GetUserMachineId();
        var client     = this.GetUserClient();
        var appId      = this.GetUserAppId();
        var app        = clientApps.Value.Find(appId, client);
        var deviceType = ClientIdentity.DeviceType(app, client);
        var country    = this.GetUserRegion() is { Length: > 0 } known && known != GeoLocation.UnknownCountry ? known : null;
        var region     = country ?? "unknown";
        var ip         = this.GetUserIp() ?? "unknown";
        var now        = DateTimeOffset.UtcNow;

        // Decided before the row is written, because once it is this device is a known one.
        var firstSightOfDevice = false;
        var knownElsewhere     = false;

        await using var ctx = await context.CreateDbContextAsync();

        try
        {
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
                firstSightOfDevice = true;
                knownElsewhere     = await ctx.DeviceHistories.AnyAsync(x => x.UserId == userId && x.MachineId != machineId);

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

        // What the sign-in means beyond the row: a courtesy mail for a device never seen before, and
        // the end of an inactivity deletion if one is counting down. Neither may fail the sign-in that
        // caused it, so each is fenced on its own.
        var evidence = new SignInEvidence
        {
            Ip      = ip,
            Country = country,
            City    = this.GetUserCity(),
            Client  = ClientIdentity.Describe(app, client),
            At      = now
        };

        // The very first device an account is ever seen on is the one it registered from, and a mail
        // saying "new device" a second after the welcome mail is noise; the second device onwards is
        // news. An account with no history at all — older than the history table, or one whose rows
        // were pruned — is therefore told about its second device, not its first, which is the
        // conservative side.
        if (firstSightOfDevice && knownElsewhere)
        {
            try
            {
                var who = await ctx.Users
                   .Where(x => x.Id == userId)
                   .Select(x => new { x.Email, x.DisplayName })
                   .FirstOrDefaultAsync();

                if (who is not null && !string.IsNullOrWhiteSpace(who.Email))
                    await GrainFactory.GetGrain<IEmailManager>(Guid.Empty).SendNewDeviceSignInAsync(
                        who.Email, who.DisplayName, evidence.Ip, evidence.Location, evidence.Client, evidence.At);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not announce the new device of {UserId}", userId);
            }
        }

        try
        {
            await GrainFactory.GetGrain<IAccountDeletionGrain>(userId).NoticeSignInAsync(evidence);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not tell the deletion grain that {UserId} signed in", userId);
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

    /// <inheritdoc cref="IUserGrain.AggregateAndBroadcastStatusAsync(CancellationToken)"/>
    public ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default)
        => GrainFactory.GetGrain<IUserPresenceGrain>(this.GetPrimaryKey()).AggregateAndBroadcastStatusAsync(ct);

    /// <inheritdoc cref="IUserGrain.AggregateAndBroadcastStatusAsync(Guid[],CancellationToken)"/>
    public ValueTask AggregateAndBroadcastStatusAsync(Guid[] seedSpaces, CancellationToken ct = default)
        => GrainFactory.GetGrain<IUserPresenceGrain>(this.GetPrimaryKey())
           .AggregateAndBroadcastStatusAsync(seedSpaces, ct);

    /// <summary>
    /// The mirror of the transition fan-out: a session that has just connected has missed every status
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
    /// How many friend-scoped publishes are in flight at once.
    /// </summary>
    /// <remarks>
    /// The seed sits on the connect path a session grain is awaiting, so it may not be sequential;
    /// and it is per user, so it may not be unbounded, or one very sociable account becomes a
    /// thundering herd of its own. Its twin on the transition side moved to
    /// <c>UserPresenceGrain</c> with the fan-out, and carries the same number for the same reason.
    /// </remarks>
    private const int FriendFanOutConcurrency = 16;

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