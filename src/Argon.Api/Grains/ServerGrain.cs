namespace Argon.Grains;

using Argon.Features.Rpc;
using Features.Repositories;
using Orleans.Providers;
using Persistence.States;
using Shared.Servers;

[StorageProvider(ProviderName = IFusionSessionGrain.StorageId)]
public class ServerGrain(
    IGrainFactory grainFactory,
    IDbContextFactory<ApplicationDbContext> context,
    IServerRepository serverRepository) : Grain<RealtimeServerGrainState>, IServerGrain
{
    private IArgonStream<IArgonEvent> _serverEvents;

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await ReadStateAsync();
        _serverEvents = await this.Streams().CreateServerStream();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        => base.WriteStateAsync();


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

        var copy = server with { };
        server.Name         = input.Name ?? server.Name;
        server.Description  = input.Description ?? server.Description;
        server.AvatarFileId = input.AvatarUrl ?? server.AvatarFileId;
        ctx.Servers.Update(server);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ServerModified(ObjDiff.Compare(copy, server)));
        return await ctx.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var members = await ctx.UsersToServerRelations.Where(x => x.ServerId == this.GetPrimaryKey())
           .ToListAsync();

        return members.Select(x => new RealtimeServerMember
        {
            Member = x,
            Status = State.UserStatuses.TryGetValue(x.UserId, out var status) ? status : UserStatus.Offline
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
            ServerId = this.GetPrimaryKey(),
            UserId   = userId
        });
        await ctx.SaveChangesAsync();
        await UserJoined(userId);
    }

    public async ValueTask UserJoined(Guid userId)
    {
        await _serverEvents.Fire(new JoinToServerUser(userId));
        await SetUserStatus(userId, UserStatus.Offline);
    }

    public async ValueTask SetUserStatus(Guid userId, UserStatus status)
    {
        State.UserStatuses[userId] = status;
        await _serverEvents.Fire(new UserChangedStatus(userId, status, PropertyBag.Empty));
    }

    public async Task DeleteServer()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var server = await ctx.Servers.FirstAsync(s => s.Id == this.GetPrimaryKey());
        ctx.Servers.Remove(server);
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