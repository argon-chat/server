namespace Argon.Grains;

using System.Diagnostics;
using Features.Logic;
using Orleans;
using Orleans.Concurrency;
using Services;

[StatelessWorker]
public class UserGrain(
    IPasswordHashingService passwordHashingService,
    IDbContextFactory<ApplicationDbContext> context,
    IUserPresenceService presenceService,
    ILogger<IUserGrain> logger) : Grain, IUserGrain
{
    public async Task<User> UpdateUser(UserEditInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var user = await ctx.Users.FirstAsync(x => x.Id == this.GetPrimaryKey());
        user.DisplayName  = input.DisplayName ?? user.DisplayName;
        user.AvatarFileId = input.AvatarId ?? user.AvatarFileId;
        ctx.Users.Update(user);
        await ctx.SaveChangesAsync();

        var userServers = await GetMyServersIds();

        await Task.WhenAll(userServers
           .Select(id => GrainFactory
               .GetGrain<IServerGrain>(id)
               .DoUserUpdatedAsync()
               .AsTask())
           .ToArray());

        return user;
    }

    public async Task<User> GetMe()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .AsNoTracking()
           .FirstAsync(user => user.Id == this.GetPrimaryKey());
    }

    public async Task<List<Server>> GetMyServers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .AsNoTracking()
           .Include(user => user.ServerMembers)
           .ThenInclude(usersToServerRelation => usersToServerRelation.Server)
           .ThenInclude(x => x.Users)
           .Include(user => user.ServerMembers)
           .ThenInclude(usersToServerRelation => usersToServerRelation.Server)
           .ThenInclude(x => x.Channels)
           .Where(x => x.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.Server)
           .AsSplitQuery()
           .ToListAsync();
    }

    public async Task<List<Guid>> GetMyServersIds()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Users
           .AsNoTracking()
           .Include(user => user.ServerMembers)
           .Where(u => u.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.ServerId)
           .ToListAsync();
    }

    public async ValueTask BroadcastPresenceAsync(UserActivityPresence presence)
    {
        await presenceService.BroadcastActivityPresence(presence, this.GetPrimaryKey(), Guid.Empty);
        var servers = await GetMyServersIds();
        foreach (var server in servers)
            await GrainFactory
               .GetGrain<IServerGrain>(server)
               .SetUserPresence(this.GetPrimaryKey(), presence);
    }

    public async ValueTask RemoveBroadcastPresenceAsync()
    {
        logger.LogInformation("Called remove broadcast presence for {userId}", this.GetPrimaryKey());
        await presenceService.RemoveActivityPresence(this.GetPrimaryKey());

        var servers = await GetMyServersIds();
        foreach (var server in servers)
            await GrainFactory
               .GetGrain<IServerGrain>(server)
               .RemoveUserPresence(this.GetPrimaryKey());
    }

    public async ValueTask CreateSocialBound(SocialKind kind, string userData, string socialId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        await ctx.SocialIntegrations.AddAsync(new UserSocialIntegration()
        {
            Kind     = kind,
            SocialId = socialId,
            UserData = userData,
            Id       = Guid.NewGuid(),
            UserId   = this.GetPrimaryKey()
        });
        await ctx.SaveChangesAsync();
    }

    public async ValueTask<List<UserSocialIntegrationDto>> GetMeSocials()
    {
        await using var ctx = await context.CreateDbContextAsync();
        return await ctx.SocialIntegrations.AsNoTracking().Where(x => x.UserId == this.GetPrimaryKey()).ToListAsync().ToDto();
    }

    public async ValueTask<bool> DeleteSocialBoundAsync(string kind, Guid socialId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        try
        {
            var result = await ctx.SocialIntegrations.Where(x => x.Id == socialId).ExecuteDeleteAsync();
            return result == 1;
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed delete social bound by {socialId}", socialId);
            return false;
        }
    }

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
                await ctx.DeviceHistories.AddAsync(new UserDeviceHistory
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
}