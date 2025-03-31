namespace Argon.Grains;

using Argon.Features.Rpc;
using Features.Repositories;
using Orleans.GrainDirectory;
using Persistence.States;

[GrainDirectory(GrainDirectoryName = "servers")]
public class ServerGrain(
    [PersistentState("realtime-server", IUserSessionGrain.StorageId)]
    IPersistentState<RealtimeServerGrainState> state,
    IGrainFactory grainFactory,
    IDbContextFactory<ApplicationDbContext> context,
    IServerRepository serverRepository) : Grain, IServerGrain
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

    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var members = await ctx
           .UsersToServerRelations
           .Include(x => x.User)
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .ToListAsync();

        return members.Select(x => new RealtimeServerMember
        {
            Member = x,
            Status = state.State.UserStatuses.TryGetValue(x.UserId, out var item)
                ? (item.lastSetStatus - DateTime.UtcNow).TotalMinutes < 10 ? item.Status : UserStatus.Offline
                : UserStatus.Offline
        }).ToList();
    }

    public async Task<List<RealtimeChannel>> GetChannels()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var channels = await ctx.Channels
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .ToListAsync();

        var results = await Task.WhenAll(channels.Select(async x => new RealtimeChannel()
        {
            Channel = x,
            Users   = await grainFactory.GetGrain<IChannelGrain>(x.Id).GetMembers()
        }).ToList());

        return results.ToList();
    }

    public async ValueTask DoJoinUserAsync(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        await ctx.UsersToServerRelations.AddAsync(new ServerMember
        {
            Id       = Guid.NewGuid(),
            ServerId = this.GetPrimaryKey(),
            UserId   = userId
        });
        await ctx.SaveChangesAsync();
        await UserJoined(userId);
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