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

    public async Task<ArgonUser> GetAsArgonUser()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users
           .AsNoTracking()
           .Include(u => u.BotEntity)
           .FirstAsync(u => u.Id == this.GetPrimaryKey());

        return UserEntity.Map(user);
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

        return result.Select(x => new ArgonSpaceBase(x.Id, x.Name, x.Description!, x.AvatarFileId, x.TopBannedFileId, x.BoostCount, x.BoostLevel)).ToList();
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

    public async ValueTask BroadcastPresenceAsync(UserActivityPresence presence)
    {
        await presenceService.BroadcastActivityPresence(presence, this.GetPrimaryKey(), Guid.Empty);
        var servers = await GetMyServersIds();
        await Task.WhenAll(servers.Select(server =>
            GrainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserPresence(this.GetPrimaryKey(), presence)));
    }

    public async ValueTask RemoveBroadcastPresenceAsync()
    {
        logger.LogInformation("Called remove broadcast presence for {userId}", this.GetPrimaryKey());
        await presenceService.RemoveActivityPresence(this.GetPrimaryKey());

        var servers = await GetMyServersIds();
        await Task.WhenAll(servers.Select(server =>
            GrainFactory
               .GetGrain<ISpaceGrain>(server)
               .RemoveUserPresence(this.GetPrimaryKey())));
    }

    //public async ValueTask CreateSocialBound(SocialKind kind, string userData, string socialId)
    //{
    //    await using var ctx = await context.CreateDbContextAsync();

    //    await ctx.SocialIntegrations.AddAsync(new UserSocialIntegration()
    //    {
    //        Kind     = kind,
    //        SocialId = socialId,
    //        UserData = userData,
    //        Id       = Guid.NewGuid(),
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
    public async ValueTask UpdateUserDeviceHistory()
    {
        await using var ctx = await context.CreateDbContextAsync();

        try
        {
            logger.LogWarning("UpdateUserDeviceHistory, {region}, {ip}, {userId}, {machineId}", this.GetUserRegion(), this.GetUserIp(),
                this.GetPrimaryKey(), this.GetUserMachineId());
            var history = await ctx.DeviceHistories.FirstOrDefaultAsync(x
                => x.UserId == this.GetPrimaryKey() && x.MachineId == this.GetUserMachineId());


            if (history is not null)
            {
                history.LastKnownIP   = this.GetUserIp() ?? "unknown";
                history.RegionAddress = this.GetUserRegion() ?? "unknown";
                history.LastLoginTime = DateTimeOffset.UtcNow;

                ctx.Update(history);
            }
            else
            {
                await ctx.DeviceHistories.AddAsync(new UserDeviceHistoryEntity
                {
                    AppId         = "unknown",
                    DeviceType    = DeviceTypeKind.WindowsDesktop,
                    LastKnownIP   = this.GetUserIp() ?? "unknown",
                    LastLoginTime = DateTimeOffset.UtcNow,
                    MachineId     = this.GetUserMachineId(),
                    RegionAddress = this.GetUserRegion() ?? "unknown",
                    UserId        = this.GetPrimaryKey()
                });
            }

            var result = await ctx.SaveChangesAsync();

            logger.LogWarning("UpdateUserDeviceHistory, saved {count}", result);
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
            var userId = this.GetUserId();
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
        var userId = this.GetUserId();
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
                    "Avatar upload rejected for user {UserId}, file {FileId}, stages={Stages}",
                    userId, fileInfo.FileId, modResult.StagesUsed);

                throw new ContentViolationException("Avatar rejected by content moderation");
            }
        }

        await UpdateFileIdFor(kind, fileInfo.FileId, fileInfo.S3Key, ct);
    }

    public async ValueTask<LockedAuthStatus> GetLimitationForUser()
    {
        var user = await GetMe();

        if (user.LockdownReason is LockdownReason.NONE)
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
        var             userId = this.GetUserId();

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
        
        var servers = await GetMyServersIds(ct);
        await Task.WhenAll(servers.Select(server =>
            GrainFactory
               .GetGrain<ISpaceGrain>(server)
               .SetUserStatus(userId, aggregatedStatus)));
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
                Id = Guid.NewGuid(),
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
}