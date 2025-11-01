namespace Argon.Grains;

using Api.Features.CoreLogic.Messages;
using Argon.Api.Features.Bus;
using Cassandra.Core;
using Microsoft.EntityFrameworkCore;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Persistence.States;
using Sfu;

[GrainDirectory(GrainDirectoryName = "channels")]
public class ChannelGrain(
    [PersistentState("channel-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<ChannelGrainState> state,
    IArgonSelectiveForwardingUnit sfu,
    IDbContextFactory<ApplicationDbContext> context,
    IMessagesLayout messagesLayout) : Grain, IChannelGrain
{
    private IDistributedArgonStream<IArgonEvent> _userStateEmitter = null!;

    private ChannelEntity  _self     { get; set; }
    private ArgonSpaceId   SpaceId   => new(_self.SpaceId);
    private ArgonChannelId ChannelId => new(SpaceId, this.GetPrimaryKey());

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _self = await Get();

        _userStateEmitter = await this.Streams().CreateServerStreamFor(SpaceId.id);

        await state.ReadStateAsync(cancellationToken);

        state.State.Users.Clear();

        await state.WriteStateAsync(cancellationToken);
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await Task.WhenAll(state.State.Users.Select(x => Leave(x.Key)));
        await _userStateEmitter.DisposeAsync();
    }
    
    public async Task<List<RealtimeChannelUser>> GetMembers()
        => state.State.Users.Select(x => x.Value).ToList();

    [OneWay]
    public Task ClearChannel()
    {
        GrainContext.Deactivate(new DeactivationReason(DeactivationReasonCode.None, ""));
        return Task.CompletedTask;
    }

    [OneWay]
    public async ValueTask OnTypingEmit()
        => await _userStateEmitter.Fire(new UserTypingEvent(this.GetUserId(), SpaceId.id, ChannelId.channelId));

    [OneWay]
    public async ValueTask OnTypingStopEmit()
        => await _userStateEmitter.Fire(new UserStopTypingEvent(this.GetUserId(), SpaceId.id, ChannelId.channelId));

    public async Task<bool> KickMemberFromChannel(Guid memberId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return false;

        await using var ctx = await context.CreateDbContextAsync();
        
        var userId = this.GetUserId();

        if (!await HasAccessAsync(ctx, userId, ArgonEntitlement.KickMember))
            return false;

        return await sfu.KickParticipantAsync(new ArgonUserId(memberId), new ArgonChannelId(this.SpaceId, this.GetPrimaryKey()));
    }


    // TODO
    private async Task<bool> HasAccessAsync(ApplicationDbContext ctx, Guid callerId, ArgonEntitlement requiredEntitlement)
    {
        var invoker = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.SpaceId == SpaceId.id && x.UserId == callerId)
           .Include(x => x.SpaceMemberArchetypes)
           .ThenInclude(x => x.Archetype)
           .FirstOrDefaultAsync();

        if (invoker is null)
            return false;

        var invokerArchetypes = invoker
           .SpaceMemberArchetypes
           .Select(x => x.Archetype)
           .ToList();

        return invokerArchetypes.Any(x
            => x.Entitlement.HasFlag(requiredEntitlement));
    }

    public async Task<Either<string, JoinToChannelError>> Join()
    {
        if (_self.ChannelType != ChannelType.Voice)
            return JoinToChannelError.CHANNEL_IS_NOT_VOICE;

        var userId = this.GetUserId();

        if (state.State.Users.ContainsKey(userId))
            await _userStateEmitter.Fire(new LeavedFromChannelUser(SpaceId.id, this.GetPrimaryKey(), userId));
        else
        {
            state.State.Users.Add(userId, new RealtimeChannelUser(userId, ChannelMemberState.NONE));
            await state.WriteStateAsync();
        }

        await _userStateEmitter.Fire(new JoinedToChannelUser(SpaceId.id, this.GetPrimaryKey(), userId));

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        return await sfu.IssueAuthorizationTokenAsync(userId, ChannelId, SfuPermission.DefaultUser);
    }

    public async Task<RtcEndpoint> GetConfiguration()
        => await sfu.GetRtcEndpointAsync();

    public async Task Leave(Guid userId)
    {
        state.State.Users.Remove(userId);
        await _userStateEmitter.Fire(new LeavedFromChannelUser(SpaceId.id, this.GetPrimaryKey(), userId));
        await state.WriteStateAsync();

        if (state.State.Users.Count == 0)
            this.DelayDeactivation(TimeSpan.MinValue);
    }

    public async Task<ChannelEntity> UpdateChannel(ChannelInput input)
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

    public async Task<List<ArgonMessageEntity>> QueryMessages(long? @from, int limit)
        => await messagesLayout.QueryMessages(_self.SpaceId, this.GetPrimaryKey(), @from, limit);

    public async Task<long> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo)
    {
        if (_self.ChannelType != ChannelType.Text) throw new InvalidOperationException("Channel is not text");
        var senderId = this.GetUserId();
        var message = new ArgonMessageEntity
        {
            SpaceId   = _self.SpaceId,
            ChannelId = this.GetPrimaryKey(),
            CreatorId = senderId,
            Entities  = entities,
            Text      = text,
            CreatedAt = DateTimeOffset.UtcNow,
            Reply     = replyTo,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var dup = await messagesLayout.CheckDuplicationAsync(message, randomId);

        if (dup is not null) return dup.Value;

        var msgId = await messagesLayout.ExecuteInsertMessage(message, randomId);

        message.MessageId = msgId;

        await _userStateEmitter.Fire(new MessageSent(_self.SpaceId, message.ToDto()));
        return msgId;
    }

    private async Task<ChannelEntity> Get()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
    }
}