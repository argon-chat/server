namespace Argon.Grains;

using Features.Rpc;
using Persistence.States;
using Sfu;
using Servers;

public class ChannelGrain(
    IArgonSelectiveForwardingUnit sfu,
    IDbContextFactory<ApplicationDbContext> context) : Grain<ChannelGrainState>, IChannelGrain
{
    private IArgonStream<IArgonEvent> _userStateEmitter = null!;


    private Channel        _self     { get; set; }
    private ArgonServerId  ServerId  => new(_self.ServerId);
    private ArgonChannelId ChannelId => new(ServerId, this.GetPrimaryKey());

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _self = await GetChannel();

        _userStateEmitter = await this.Streams().CreateServerStream();
    }

    public async Task<List<RealtimeChannelUser>> GetMembers()
        => State.Users.Select(x => x.Value).ToList();


    // no needed send StreamId too, id is can be computed
    public async Task<Maybe<RealtimeToken>> Join(Guid userId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return Maybe<RealtimeToken>.None();

        if (State.Users.ContainsKey(userId))
        {
            //await _userStateEmitter.Fire(
            //    new OnChannelUserChangedState(userId, ON_LEAVED));
        }
        else
        {
            State.Users.Add(userId, new RealtimeChannelUser()
            {
                UserId = userId,
                State = ChannelMemberState.NONE
            });
            await WriteStateAsync();
        }

        //await _userStateEmitter.Fire(
        //    new OnChannelUserChangedState(userId, ON_JOINED));

        return await sfu.IssueAuthorizationTokenAsync(userId, ChannelId, SfuPermission.DefaultUser);
    }

    public async Task Leave(Guid userId)
    {
        State.Users.Remove(userId);
        //await _userStateEmitter.OnNextAsync(new(userId, ON_LEAVED));
        await sfu.KickParticipantAsync(userId, ChannelId);
        await WriteStateAsync();
    }

    public async Task<Channel> GetChannel()
    {
        var channel = await Get();
        //channel.ConnectedUsers = state.State.Users;
        return channel;
    }

    public async Task<Channel> UpdateChannel(ChannelInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var channel = await Get();
        channel.Name        = input.Name;
        channel.Description = input.Description ?? channel.Description;
        channel.ChannelType = input.ChannelType;
        ctx.Channels.Update(channel);
        await ctx.SaveChangesAsync();
        return (await Get());
    }

    private async Task<Channel> Get()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
    }
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