namespace Argon.Api.Grains;

using AutoMapper;
using Contracts;
using Contracts.etc;
using Entities;
using Features.Sfu;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Orleans.Streams;
using Persistence.States;
using static ChannelUserChangedStateEvent;
using ArgonChannelId = Features.Sfu.ArgonChannelId;
using ArgonServerId = Features.Sfu.ArgonServerId;
using RealtimeToken = Features.Sfu.RealtimeToken;

public class ChannelGrain(
    IArgonSelectiveForwardingUnit sfu,
    ApplicationDbContext context,
    [PersistentState("channelGrainState", "OrleansStorage")] IPersistentState<ChannelGrainState> state,
    IMapper mapper) : Grain, IChannelGrain
{
    private IAsyncStream<OnChannelUserChangedState> _userStateEmitter = null!;


    private ChannelDto     _self     { get; set; }
    private ArgonServerId  ServerId  => new(_self.ServerId);
    private ArgonChannelId ChannelId => new(ServerId, this.GetPrimaryKey());


    // no needed send StreamId too, id is can be computed
    public async Task<Maybe<RealtimeToken>> Join(Guid userId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return Maybe<RealtimeToken>.None();

        var user = (await context.Servers.Include(x => x.UsersToServerRelations).FirstAsync(x => x.Id == _self.ServerId)).UsersToServerRelations
           .First(x => x.UserId == userId);

        state.State.Users.Add(userId, mapper.Map<UsersToServerRelationDto>(user));
        await state.WriteStateAsync();

        await _userStateEmitter.OnNextAsync(new OnChannelUserChangedState(userId, ON_JOINED));

        return await sfu.IssueAuthorizationTokenAsync(userId, ChannelId, SfuPermission.DefaultUser);
    }

    public async Task Leave(Guid userId)
    {
        state.State.Users.Remove(userId);
        await _userStateEmitter.OnNextAsync(new OnChannelUserChangedState(userId, ON_LEAVED));
        await sfu.KickParticipantAsync(userId, ChannelId);
        await state.WriteStateAsync();
    }

    public async Task<ChannelDto> GetChannel()
    {
        var channel = mapper.Map<ChannelDto>(await Get());
        channel.ConnectedUsers = state.State.Users;
        return channel;
    }

    public async Task<ChannelDto> UpdateChannel(ChannelInput input)
    {
        var channel = await Get();
        channel.Name        = input.Name;
        channel.AccessLevel = input.AccessLevel;
        channel.Description = input.Description ?? channel.Description;
        channel.ChannelType = input.ChannelType;
        context.Channels.Update(channel);
        await context.SaveChangesAsync();
        return mapper.Map<ChannelDto>(await Get());
    }

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _self = await GetChannel();

        var streamProvider = this.GetStreamProvider("default");

        var streamId = StreamId.Create(_self.ServerId.ToString("N"), this.GetPrimaryKey());

        _userStateEmitter = streamProvider.GetStream<OnChannelUserChangedState>(streamId);
    }

    private async Task<Channel> Get() => await context.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
}

public enum ChannelUserChangedStateEvent
{
    ON_JOINED,
    ON_LEAVED,
    ON_MUTED,
    ON_MUTED_ALL,
    ON_ENABLED_VIDEO,
    ON_DISABLED_VIDEO,
    ON_ENABLED_STREAMING,
    ON_DISABLED_STREAMING
}

[MemoryPackable, GenerateSerializer]
public partial record OnChannelUserChangedState([property: Id(0)] Guid userId, [property: Id(1)] ChannelUserChangedStateEvent eventKind);