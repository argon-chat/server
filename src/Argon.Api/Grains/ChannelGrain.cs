namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Argon.Features.Messages;
using Cassandra.Core;
using Microsoft.EntityFrameworkCore;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Persistence.States;
using Servers;
using Sfu;

[GrainDirectory(GrainDirectoryName = "channels")]
public class ChannelGrain(
    [PersistentState("channel-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<ChannelGrainState> state,
    IArgonSelectiveForwardingUnit sfu,
    IDbContextFactory<ApplicationDbContext> context,
    ICassandraDbContextFactory<ArgonCassandraDbContext> cassandraContext) : Grain, IChannelGrain
{
    private IDistributedArgonStream<IArgonEvent> _userStateEmitter = null!;


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
        //await using var ctx = await context.CreateDbContextAsync();
        //var messages = await ctx.Messages
        //   .Where(m => m.ChannelId == this.GetPrimaryKey())
        //   .OrderByDescending(m => m.MessageId)
        //   .Skip(offset)
        //   .Take(count)
        //   .AsNoTracking()
        //   .ToListAsync();

        //return messages;
        return [];
    }

    public async Task<List<ArgonMessage>> QueryMessages(ulong? @from, int limit)
    {
        await using var ctx = await cassandraContext.CreateDbContextAsync();

        var processor = new MessageProcessor(ctx.Context);

        return await processor.QueryMessages(_self.ServerId, this.GetPrimaryKey(), @from, limit);
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
        => await _userStateEmitter.Fire(new UserTypingEvent(this.GetUserId(), ServerId.id, ChannelId.channelId));

    [OneWay]
    public async ValueTask OnTypingStopEmit()
        => await _userStateEmitter.Fire(new UserStopTypingEvent(this.GetUserId(), ServerId.id, ChannelId.channelId));

    public async Task<bool> KickMemberFromChannel(Guid memberId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return false;

        await using var ctx = await context.CreateDbContextAsync();

        var userId = this.GetUserId();

        if (!await HasAccessAsync(ctx, userId, ArgonEntitlement.KickMember))
            return false;

        return await sfu.KickParticipantAsync(new ArgonUserId(memberId), new ArgonChannelId(this.ServerId, this.GetPrimaryKey()));
    }


    // TODO
    private async Task<bool> HasAccessAsync(ApplicationDbContext ctx, Guid callerId, ArgonEntitlement requiredEntitlement)
    {
        var invoker = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.ServerId == ServerId.id && x.UserId == callerId)
           .Include(x => x.ServerMemberArchetypes)
           .ThenInclude(x => x.Archetype)
           .FirstOrDefaultAsync();

        if (invoker is null)
            return false;

        var invokerArchetypes = invoker
           .ServerMemberArchetypes
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

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        return await sfu.IssueAuthorizationTokenAsync(userId, ChannelId, SfuPermission.DefaultUser);
    }

    public async Task Leave(Guid userId)
    {
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

    public async Task SendMessage(string text, List<MessageEntity> entities, ulong? replyTo)
    {
        if (_self.ChannelType != ChannelType.Text) throw new InvalidOperationException("Channel is not text");
        var senderId = this.GetUserId();

        await using var csCtx = await cassandraContext.CreateDbContextAsync();

        var processor = new MessageProcessor(csCtx.Context);

        await using var ctx = await context.CreateDbContextAsync();

        var msgId = await ctx.GenerateNextMessageId(_self.ServerId, this.GetPrimaryKey());

        var rand = (unchecked((ulong)Random.Shared.NextInt64(long.MinValue, long.MaxValue)));

        var message = new ArgonMessage
        {
            ServerId  = _self.ServerId,
            ChannelId = this.GetPrimaryKey(),
            CreatorId = senderId,
            Entities  = entities,
            Text      = text,
            MessageId = msgId,
            CreatedAt = DateTimeOffset.UtcNow,
            Reply     = replyTo,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var dup = await processor.CheckDuplicationAsync(message, rand);

        if (dup) return;

        await processor.ExecuteInsertMessage(msgId, message, rand);

        await _userStateEmitter.Fire(new MessageSent(message.ToDto()));
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