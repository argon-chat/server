namespace Argon.Api.Grains;

using ActualLab.Collections;
using Contracts;
using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Argon.Api.Features.Rpc;
using Contracts.Models;
using Persistence.States;
using Argon.Api.Features.Repositories;
using Argon.Features;

public class ServerGrain(
    IGrainFactory grainFactory,
    ApplicationDbContext context,
    [PersistentState(nameof(RealtimeServerGrainState), IFusionSessionGrain.StorageId)]
    IPersistentState<RealtimeServerGrainState> realtimeState,
    IServerRepository serverRepository) : Grain, IServerGrain
{
    private IArgonStream<IArgonEvent> _serverEvents;

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
        => _serverEvents = await this.Streams().CreateServerStream();


    public async Task<Either<Server, ServerCreationError>> CreateServer(ServerInput input, Guid creatorId)
    {
        if (string.IsNullOrEmpty(input.Name))
            return ServerCreationError.BAD_MODEL;

        await serverRepository.CreateAsync(this.GetPrimaryKey(), input, creatorId);
        await UserJoined(creatorId);
        return await GetServer();
    }

    public Task<Server> GetServer() => GetAsync();

    public async Task<Server> UpdateServer(ServerInput input)
    {
        var server = await GetAsync();

        var copy = server with { };
        server.Name         = input.Name ?? server.Name;
        server.Description  = input.Description ?? server.Description;
        server.AvatarFileId = input.AvatarUrl ?? server.AvatarFileId;
        context.Servers.Update(server);
        await context.SaveChangesAsync();
        await _serverEvents.Fire(new ServerModified(ObjDiff.Compare(copy, server)));
        return await GetAsync();
    }

    public async ValueTask UserJoined(Guid userId)
    {
        await _serverEvents.Fire(new JoinToServerUser(userId));
        await SetUserStatus(userId, UserStatus.Offline);
    }

    public async ValueTask SetUserStatus(Guid userId, UserStatus status)
    {
        realtimeState.State.UserStatuses[userId] = status;
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

    private async Task<Server> GetAsync() =>
        await context.Servers
           .Include(x => x.Channels)
           .Include(x => x.Users)
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
}