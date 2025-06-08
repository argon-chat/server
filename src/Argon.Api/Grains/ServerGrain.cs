namespace Argon.Grains;

using Api.Features.Utils;
using Argon.Features.Rpc;
using Features.Logic;
using Features.Repositories;
using Orleans.GrainDirectory;
using Persistence.States;
using Services;

[GrainDirectory(GrainDirectoryName = "servers")]
public class ServerGrain(
    [PersistentState("realtime-server", IUserSessionGrain.StorageId)]
    IPersistentState<RealtimeServerGrainState> state,
    IGrainFactory grainFactory,
    IDbContextFactory<ApplicationDbContext> context,
    IServerRepository serverRepository,
    IUserPresenceService userPresence) : Grain, IServerGrain
{
    private IArgonStream<IArgonEvent> _serverEvents;

    public async override Task OnActivateAsync(CancellationToken ct)
    {
        await state.ReadStateAsync(ct);
        state.State.UserStatuses.Clear();
        await state.WriteStateAsync(ct);

        _serverEvents = await this.Streams().CreateServerStreamFor(this.GetPrimaryKey());
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
        => state.WriteStateAsync(ct);


    public async Task<Either<Server, ServerCreationError>> CreateServer(ServerInput input, Guid creatorId)
    {
        if (string.IsNullOrEmpty(input.Name))
            return ServerCreationError.BAD_MODEL;

        await serverRepository.CreateAsync(this.GetPrimaryKey(), input, creatorId);
        await UserJoined(creatorId);
        return await GetServer();
    }

    public async Task<Server> GetServer()
    {
        await using var ctx = await context.CreateDbContextAsync();
        return await ctx.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<Server> UpdateServer(ServerInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var server = await ctx.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());

        var copy = server with
        {
        };
        server.Name         = input.Name ?? server.Name;
        server.Description  = input.Description ?? server.Description;
        server.AvatarFileId = input.AvatarUrl ?? server.AvatarFileId;
        ctx.Servers.Update(server);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ServerModified( /*ObjDiff.Compare(copy, server)*/[]));
        return await ctx.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<RealtimeServerMember> GetMember(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var x = await ctx
           .UsersToServerRelations
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .Where(x => x.UserId == userId)
           .Include(x => x.User)
           .Include(x => x.ServerMemberArchetypes)
           .FirstAsync();


        return new RealtimeServerMember
        {
            Member = x.ToDto(),
            Status = state.State.UserStatuses.TryGetValue(x.UserId, out var item)
                ? (item.lastSetStatus - DateTime.UtcNow).TotalMinutes < 2 ? item.Status : UserStatus.Offline
                : UserStatus.Offline,
            Presence = await userPresence.GetUsersActivityPresence(x.UserId)
        };
    }

    public async ValueTask SetUserPresence(Guid userId, UserActivityPresence presence)
        => await _serverEvents.Fire(new OnUserPresenceActivityChanged(userId, presence));

    public async ValueTask RemoveUserPresence(Guid userId)
        => await _serverEvents.Fire(new OnUserPresenceActivityRemoved(userId));

    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var members = await ctx
           .UsersToServerRelations
           .Include(x => x.User)
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .Include(x => x.ServerMemberArchetypes)
           .ToListAsync();

        var ids        = members.Select(x => x.UserId).ToList();
        var activities = await userPresence.BatchGetUsersActivityPresence(ids);

        return members.Select(x => new RealtimeServerMember
        {
            Member = x.ToDto(),
            Status = state.State.UserStatuses.TryGetValue(x.UserId, out var item)
                ? (item.lastSetStatus - DateTime.UtcNow).TotalMinutes < 15 ? item.Status : UserStatus.Offline
                : UserStatus.Offline,
            Presence = activities.TryGetValue(x.UserId, out var presence) ? presence : null
        }).ToList();
    }

    public async Task<List<RealtimeChannel>> GetChannels(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var serverMember   = await ctx.UsersToServerRelations.FirstAsync(x => x.UserId == userId && x.ServerId == this.GetPrimaryKey());
        var serverMemberId = serverMember.Id;
        var serverId       = this.GetPrimaryKey();

        var member = await ctx.UsersToServerRelations
           .Include(m => m.ServerMemberArchetypes)
           .ThenInclude(sma => sma.Archetype)
           .FirstAsync(m => m.Id == serverMemberId && m.ServerId == serverId);

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);

        var channels = await ctx.Channels
           .Where(c => c.ServerId == serverId)
           .Include(c => c.EntitlementOverwrites)
           .ToListAsync();

        var c = channels
           .Where(c =>
            {
                var finalPerms = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, c);
                return EntitlementAnalyzer.IsEntitlementSatisfied(finalPerms, ArgonEntitlement.ViewChannel);
            })
           .ToList();

        var results = await Task.WhenAll(c.Select(async x => new RealtimeChannel()
        {
            Channel = x,
            Users   = await grainFactory.GetGrain<IChannelGrain>(x.Id).GetMembers()
        }).ToList());

        return results.ToList();
    }

    public async ValueTask DoJoinUserAsync(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var alreadyExist = await ctx.UsersToServerRelations.AnyAsync(x => x.ServerId == this.GetPrimaryKey() && x.Id == userId);

        if (alreadyExist)
            return;

        await ctx.UsersToServerRelations.AddAsync(new ServerMember
        {
            Id       = Guid.NewGuid(),
            ServerId = this.GetPrimaryKey(),
            UserId   = userId
        });
        await ctx.SaveChangesAsync();
        await UserJoined(userId);
    }

    public async ValueTask DoUserUpdatedAsync(Guid userId)
    {
        await using var ctx  = await context.CreateDbContextAsync();
        var             user = await ctx.Users.FirstAsync(x => x.Id == userId);
        await _serverEvents.Fire(new UserUpdated(user.ToDto()));
    }

    public async ValueTask<UserProfileDto> PrefetchProfile(Guid userId, Guid caller)
    {
        await using var ctx     = await context.CreateDbContextAsync();
        List<Guid>      userIds = [userId, caller];
        var targetProfile = await ctx.Servers
           .SelectMany(server => server.Users)
           .Where(member => userIds.Contains(member.UserId))
           .Select(member => member.User.Profile)
           .FirstAsync(x => x.UserId == userId)
           .ToDto();
        return targetProfile;
    }


    public async ValueTask UserJoined(Guid userId)
    {
        await _serverEvents.Fire(new JoinToServerUser(userId));
        await SetUserStatus(userId, UserStatus.Online);
    }

    public async ValueTask SetUserStatus(Guid userId, UserStatus status)
    {
        state.State.UserStatuses[userId] = (DateTime.UtcNow, status);
        await _serverEvents.Fire(new UserChangedStatus(userId, status, []));
    }

    public async Task DeleteServer()
    {
        await using var ctx = await context.CreateDbContextAsync();
        //await ctx.Servers.DeleteByKeyAsync(this.GetPrimaryKey());
        await ctx.SaveChangesAsync();
    }

    public async Task<Channel> CreateChannel(ChannelInput input, Guid initiator)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var channel = new Channel
        {
            Name        = input.Name,
            CreatorId   = initiator,
            Description = input.Description,
            ChannelType = input.ChannelType,
            ServerId    = this.GetPrimaryKey()
        };
        await ctx.Channels.AddAsync(channel);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelCreated(channel));
        return channel;
    }

    public async Task DeleteChannel(Guid channelId, Guid initiator)
    {
        await using var ctx = await context.CreateDbContextAsync();

        ctx.Channels.Remove(await ctx.Channels.FindAsync(channelId)!);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelRemoved(channelId));
    }
}