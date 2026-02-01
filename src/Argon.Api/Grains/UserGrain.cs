namespace Argon.Grains;

using Features.Logic;
using Features.MediaStorage;
using ion.runtime;
using Orleans;
using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class UserGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IUserPresenceService presenceService,
    ILogger<IUserGrain> logger,
    IKineticaFSApi kineticaFs) : Grain, IUserGrain
{
    public async Task<UserEntity> UpdateUser(UserEditInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstAsync(x => x.Id == this.GetPrimaryKey());
        user.DisplayName  = !string.IsNullOrEmpty(input.displayName) ? input.displayName : user.DisplayName;
        user.AvatarFileId = !string.IsNullOrEmpty(input.avatarId) ? input.avatarId : user.AvatarFileId;
        ctx.Users.Update(user);
        await ctx.SaveChangesAsync();

        var userServers = await GetMyServersIds();

        await Task.WhenAll(userServers
           .Select(id => GrainFactory
               .GetGrain<ISpaceGrain>(id)
               .DoUserUpdatedAsync())
           .ToArray());

        return user;
    }

    public async Task<UserEntity> GetMe()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .AsNoTracking()
           .FirstAsync(user => user.Id == this.GetPrimaryKey());
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

        return result.Select(x => new ArgonSpaceBase(x.Id, x.Name, x.Description!, x.AvatarFileId, x.TopBannedFileId)).ToList();
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

    private uint GetLimitFor(UserFileKind kind)
        => kind switch
        {
            UserFileKind.Avatar        => 2,
            UserFileKind.ProfileHeader => 4,
            _                          => 1
        };

    public async ValueTask<Either<BlobId, UploadFileError>> BeginUploadUserFile(UserFileKind kind, CancellationToken ct = default)
    {
        try
        {
            var result = await kineticaFs.CreateUploadUrlAsync(GetLimitFor(kind), null, ct);

            return new BlobId(result);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed upload user file {kind}", kind);
            return UploadFileError.INTERNAL_ERROR;
        }
    }

    public async ValueTask CompleteUploadUserFile(Guid blobId, UserFileKind kind, CancellationToken ct = default)
    {
        var fileId = await kineticaFs.FinalizeUploadUrlAsync(blobId, ct);
        await UpdateFileIdFor(kind, fileId, ct);
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

    private ValueTask UpdateFileIdFor(UserFileKind kind, Guid fileId, CancellationToken ct = default)
        => kind switch
        {
            UserFileKind.Avatar        => UpdateAvatarFileId(fileId, ct),
            UserFileKind.ProfileHeader => UpdateProfileHeaderFileId(fileId, ct),
            _                          => throw new NotImplementedException()
        };

    private async ValueTask UpdateAvatarFileId(Guid fileId, CancellationToken ct = default)
    {
        await using var ctx    = await context.CreateDbContextAsync(ct);
        var             userId = this.GetUserId();

        var user = await ctx.Users.FirstAsync(x => x.Id == userId, cancellationToken: ct);

        var currentFileId = user.AvatarFileId;

        user.AvatarFileId = fileId.ToString();

        await ctx.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(currentFileId))
        {
            try
            {
                await kineticaFs.DecrementByFileIdAsync(currentFileId, ct);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed decrement fileId");
            }
        }

        var userServers = await GetMyServersIds(ct);

        await Task.WhenAll(userServers
           .Select(id => GrainFactory
               .GetGrain<ISpaceGrain>(id)
               .DoUserUpdatedAsync())
           .ToArray());
    }

    private async ValueTask UpdateProfileHeaderFileId(Guid fileId, CancellationToken ct = default)
    {
        await using var ctx    = await context.CreateDbContextAsync(ct);
        var             userId = this.GetUserId();

        var user = await ctx.UserProfiles.FirstAsync(x => x.UserId == userId, cancellationToken: ct);

        var currentFileId = user.BannerFileId;

        user.BannerFileId = fileId.ToString();

        await ctx.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(currentFileId))
        {
            try
            {
                await kineticaFs.DecrementByFileIdAsync(currentFileId, ct);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed decrement fileId");
            }
        }

        var userServers = await GetMyServersIds(ct);

        await Task.WhenAll(userServers
           .Select(id => GrainFactory
               .GetGrain<ISpaceGrain>(id)
               .DoUserUpdatedAsync())
           .ToArray());
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
}