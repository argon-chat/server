namespace Argon.Grains;

using Argon.Features.Rpc;
using Features.Repositories;
using Orleans.Providers;
using Persistence.States;

[StorageProvider(ProviderName = IFusionSessionGrain.StorageId)]
public class ServerGrain(
    IGrainFactory grainFactory,
    ApplicationDbContext context,
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

    public Task<Server> GetServer() => context.Servers
       .FirstAsync(s => s.Id == this.GetPrimaryKey());

    public async Task<Server> UpdateServer(ServerInput input)
    {
        var server = await context.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());

        var copy = server with { };
        server.Name         = input.Name ?? server.Name;
        server.Description  = input.Description ?? server.Description;
        server.AvatarFileId = input.AvatarUrl ?? server.AvatarFileId;
        context.Servers.Update(server);
        await context.SaveChangesAsync();
        await _serverEvents.Fire(new ServerModified(ObjDiff.Compare(copy, server)));
        return await context.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        var members = await context.UsersToServerRelations.Where(x => x.ServerId == this.GetPrimaryKey())
           .ToListAsync();

        return members.Select(x => new RealtimeServerMember
        {
            Member = x,
            Status = State.UserStatuses.TryGetValue(x.UserId, out var status) ? status : UserStatus.Offline
        }).ToList();
    }

    public async Task<List<RealtimeChannel>> GetChannels()
    {
        var channels = await context.Channels
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
        await context.UsersToServerRelations.AddAsync(new ServerMember
        {
            ServerId = this.GetPrimaryKey(),
            UserId   = userId
        });
        await context.SaveChangesAsync();
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
        var server = await context.Servers.FirstAsync(s => s.Id == this.GetPrimaryKey());
        context.Servers.Remove(server);
        await context.SaveChangesAsync();
    }

    public async Task<Channel> CreateChannel(ChannelInput input, Guid initiator)
    {
        var channel = new Channel
        {
            Name        = input.Name,
            CreatorId   = initiator,
            Description = input.Description,
            ChannelType = input.ChannelType,
            ServerId    = this.GetPrimaryKey()
        };
        await context.Channels.AddAsync(channel);
        await context.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelCreated(channel));
        return channel;
    }
}