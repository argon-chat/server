namespace Argon.Grains;

using Features.Rpc;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Persistence.States;
using Sfu;
using Servers;

[GrainDirectory(GrainDirectoryName = "channels")]
public class ChannelGrain(
    [PersistentState("channel-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<ChannelGrainState> state,
    IArgonSelectiveForwardingUnit sfu,
    IDbContextFactory<ApplicationDbContext> context) : Grain, IChannelGrain
{
    private IArgonStream<IArgonEvent> _userStateEmitter = null!;


    private Channel        _self     { get; set; }
    private ArgonServerId  ServerId  => new(_self.ServerId);
    private ArgonChannelId ChannelId => new(ServerId, this.GetPrimaryKey());

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _self = await GetChannel();

        _userStateEmitter = await this.Streams().CreateServerStreamFor(ServerId.id);

        await state.ReadStateAsync(cancellationToken);

        state.State.Users.Clear();

        await state.WriteStateAsync(cancellationToken);
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await Task.WhenAll(state.State.Users.Select(x => Leave(x.Key)));
        await _userStateEmitter.DisposeAsync();
    }

    public async Task<List<ArgonMessage>> GetMessages(int count, int offset)
    {
        await using var ctx = await context.CreateDbContextAsync();
        var messages = await ctx.Messages
           .Where(m => m.ChannelId == this.GetPrimaryKey())
           .OrderBy(m => m.MessageId)
           .Skip(offset)
           .Take(count)
           .AsNoTracking()
           .ToListAsync();

        return messages;
    }

    public async Task<List<RealtimeChannelUser>> GetMembers()
        => state.State.Users.Select(x => x.Value).ToList();

    [OneWay]
    public Task ClearChannel()
    {
        GrainContext.Deactivate(new DeactivationReason(DeactivationReasonCode.None, ""));
        return Task.CompletedTask;
    }


    public async Task<Either<string, JoinToChannelError>> Join(Guid userId, Guid sessionId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return JoinToChannelError.CHANNEL_IS_NOT_VOICE;

        if (state.State.Users.ContainsKey(userId))
            await _userStateEmitter.Fire(new LeavedFromChannelUser(userId, this.GetPrimaryKey()));
        else
        {
            state.State.Users.Add(userId, new RealtimeChannelUser()
            {
                UserId = userId,
                State  = ChannelMemberState.NONE
            });
            await state.WriteStateAsync();
        }

        await _userStateEmitter.Fire(new JoinedToChannelUser(userId, this.GetPrimaryKey()));

        //await GrainFactory.GetGrain<IUserSessionGrain>(sessionId).SetActiveChannelConnection(this.GetPrimaryKey());

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        return await sfu.IssueAuthorizationTokenAsync(userId, ChannelId, SfuPermission.DefaultUser);
    }

    public async Task Leave(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new InvalidOperationException($"Leave Guid Empty");

        state.State.Users.Remove(userId);
        await _userStateEmitter.Fire(new LeavedFromChannelUser(userId, this.GetPrimaryKey()));
        await sfu.KickParticipantAsync(userId, ChannelId);
        await state.WriteStateAsync();

        if (state.State.Users.Count == 0)
            this.DelayDeactivation(TimeSpan.MinValue);
    }

    public async Task<Channel> GetChannel()
        => await Get();

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

    public async Task SendMessage(Guid senderId, string text, List<MessageEntity> entities, ulong? replyTo)
    {
        if (_self.ChannelType != ChannelType.Text) throw new InvalidOperationException("Channel is not text");

        await using var ctx = await context.CreateDbContextAsync();

        var msgId = await ctx.GenerateNextMessageId(_self.ServerId, this.GetPrimaryKey());

        var message = new ArgonMessage()
        {
            ServerId  = _self.ServerId,
            ChannelId = this.GetPrimaryKey(),
            CreatorId = senderId,
            Entities  = entities,
            Text      = text,
            MessageId = msgId,
            CreatedAt = DateTime.UtcNow,
            Reply = replyTo
        };

        var e = await ctx.Messages.AddAsync(message);

        await ctx.SaveChangesAsync();

        await _userStateEmitter.Fire(new MessageSent(e.Entity.ToDto()));
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

[GenerateSerializer, MessagePackObject(true)]
public partial record OnChannelUserChangedState([property: Id(0)] Guid userId, [property: Id(1)] ChannelUserChangedStateEvent eventKind);