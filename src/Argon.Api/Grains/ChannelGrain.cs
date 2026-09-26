namespace Argon.Grains;

using Argon.Features.EF;
using Argon.Features.Cache;
using Api.Features.CoreLogic.Messages;
using Argon.Api.Features.Bus;
using Argon.Api.Grains.Interfaces;
using Argon.Features.Storage;
using Core.Grains.Interfaces;
using Core.Services;
using Instruments;
using Microsoft.EntityFrameworkCore;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Persistence.States;
using Sfu;
using System.Diagnostics;
using Core.Features.Transport;
using Argon.Core.Features.Logic;
using Argon.Features.BotApi;
using Argon.Features.Integrations.Crawler;
using Argon.Features.Integrations.Klipy;
using Argon.Core.Features.CoreLogic.Privacy;
using Core.Entities.Data;
using ion.runtime;
using Services.L1L2;
using Argon.Features.Orleanse.Storages;

public partial class ChannelGrain(
    [PersistentState("channel-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<ChannelGrainState> state,
    // Stores nothing; it is here so the runtime carries this across a migration. See VolatileGrainStorage.
    [PersistentState("activation", VolatileGrainStorage.ProviderName)]
    IPersistentState<ChannelActivationState> activation,
    IDbContextFactory<ApplicationDbContext> context,
    IMessagesLayout messagesLayout,
    IEntitlementChecker entitlementChecker,
    AppHubServer appHubServer,
    BotEventPublisher botEventPublisher,
    BotUserCache botUserCache,
    IS3StorageService s3,
    IKlipyService klipy,
    ILinkPreviewService linkPreviews,
    IOptions<CrawlerOptions> crawlerOptions,
    ISpaceReadCache readCache,
    IPermissionCache permissionCache,
    IArchetypeAgent archetypeAgent,
    // Same pool the read-state and replay-buffer caches use. The channel's high-water mark is a
    // cache value, not a second source of truth: it is written here and read by anyone who needs it
    // fresher than the flush interval below.
    [FromKeyedServices(RedisProfiles.Cache)] IRedisPoolConnections redisPool,
    IOptions<MessagesOptions> messageOptions,
    IOptions<CallKitOptions> callKit,
    ILogger<ChannelGrain> logger) : Grain, IChannelGrain
{
    private ChannelEntity _self     { get; set; }
    private Guid          SpaceId   => _self.SpaceId;
    private ArgonRoomId   ChannelId => new(SpaceId, this.GetPrimaryKey());

    private readonly Dictionary<Guid, IGrainTimer> _botTypingTimers = new();

    // ── Reaction buffer ──────────────────────────────────────
    private readonly Dictionary<long, List<MessageReactionData>> _reactionCache = new();
    private readonly HashSet<long> _dirtyReactions = new();
    private readonly LinkedList<long> _reactionLru = new();
    private const int MaxCachedReactionMessages = 100;
    private IGrainTimer? _reactionFlushTimer;

    // ── Channel high-water mark ──────────────────────────────
    /// <summary>
    /// The newest message id this activation has accepted, and how much of that has reached
    /// <c>ChannelLastMessages</c>.
    /// </summary>
    /// <remarks>
    /// Not part of <see cref="ChannelActivationState"/> and so deliberately does not travel: the
    /// successor of a migrated activation starts empty, which is exactly why the deactivation path
    /// flushes on migration as well. Putting it in the travelling state instead would work, but it
    /// would put a value the database is the real home of into the migration payload of every
    /// channel in the cluster during a rebalance.
    /// </remarks>
    private readonly ChannelHighWaterMark lastMessage = new();

    /// <summary>
    /// The tail of the per-send cache publishes, so they land in the order they were issued.
    /// </summary>
    /// <remarks>
    /// Each publish rents its own pooled connection, and two connections to the same Redis give no
    /// ordering relative to each other — so two sends a microsecond apart can arrive reversed and
    /// leave the cell holding the older id until something else writes it. "Last write wins" is only
    /// safe when the last write is also the newest one, and chaining is what makes that true.
    /// <para>
    /// Awaiting the write inside the turn would order it too, and would put a Redis round trip back
    /// on the exact path <see cref="DeduplicateAsync"/> was rewritten to get one off — measured at
    /// 0.93 ms against a turn with about 1.5 ms of work left in it.
    /// </para>
    /// </remarks>
    private Task lastMessagePublishTail = Task.CompletedTask;

    // ── Screencast drawing session (ephemeral, lives with the share) ──
    private const int DrawingDefaultTtlMs = 6000;

    /// <summary>Announcement mass pings of the last hour, by message id; seeded from the stored messages when first needed.</summary>
    private Dictionary<long, DateTimeOffset>? massPings;

    /// <summary>
    /// Ids handed out for the randomIds seen since this activation started, so a retry can be
    /// answered without asking Redis.
    /// </summary>
    /// <remarks>
    /// This activation is the only writer for its channel, so what it remembers is the whole truth
    /// about what it accepted. The cache entry behind it exists for the case this does not cover — an
    /// activation that moved or restarted — and <see cref="activation.State.DedupTrustedUntil"/> is when that
    /// case stops being possible.
    /// </remarks>

    /// <summary>
    /// Until this moment a retry might have been accepted by a previous activation, so the shared
    /// cache still has to be consulted. After it, any entry the cache could still hold is younger
    /// than the entry's own lifetime and therefore was written by this activation, which means it is
    /// already in <see cref="activation.State.SentByRandomId"/> and the round trip is pure cost.
    /// </summary>

    /// <summary>Start of the second the cap is currently counting, and what it has counted.</summary>


    private Task Fire<T>(T ev, CancellationToken ct = default) where T : IArgonEvent
        => appHubServer.BroadcastSpace(ev, SpaceId, ct);

    /// <summary>
    /// Hands an event to the backplane without waiting for it, and without ordering it.
    /// </summary>
    /// <remarks>
    /// Publishing took a measured 3.2 ms of a turn that totalled about 8, and it is the one part of
    /// sending a message whose result the sender does not need — the id comes from the insert.
    /// Holding the turn for it capped a channel at roughly a hundred messages a second, because every
    /// send to a channel goes through the one activation that orders them.
    /// <para>
    /// Nothing here preserves the order events reach the backplane in, and that is deliberate: a few
    /// hundred milliseconds of drift between two messages is acceptable, the same way it is in every
    /// other chat client. The sender knows its own sequence from the <c>randomId</c> it chose before
    /// the call, and <c>messageId</c> is roughly time-ordered. An ordered chain was tried first and
    /// became the next bottleneck: it moved the serialisation from the turn to the publish and cost
    /// delivery 333 ms at saturation.
    /// </para>
    /// <para>
    /// <b>The desktop client cannot absorb that drift yet.</b> Its cursor is a high-water mark — see
    /// <c>advanceCursor</c> in <c>realtimeWorker.ts</c> — so a <c>broadcastSpace</c> whose replay
    /// entry id is lower than one already seen is discarded rather than shown late, and the replay
    /// will not bring it back because the cursor has moved past it. Until that becomes a dedupe by
    /// id, drift here is occasional silent loss for that client. The window was never zero: two
    /// channels in one space have always published concurrently from separate activations.
    /// </para>
    /// <para>
    /// The cost is a weaker promise: <c>SendMessage</c> returns once the message is stored, not once
    /// the backplane has it, and a publish that fails afterwards is logged rather than surfaced. That
    /// is the guarantee the mention and last-message updates beside it already have.
    /// </para>
    /// </remarks>
    private void FireDetached<T>(T ev) where T : IArgonEvent
        => _ = Task.Run(async () =>
        {
            try
            {
                await Fire(ev);
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to broadcast {Event} for channel {ChannelId}",
                    typeof(T).Name, this.GetPrimaryKey());
            }
        });

    // Channel-scoped delivery for high-frequency channel content (messages, edits, reactions,
    // typing): reaches only clients currently viewing THIS channel, not all members of the space.
    // Space-wide events (voice membership, recording, meetings, mentions) keep using Fire().
    private Task FireChannel<T>(T ev, CancellationToken ct = default) where T : IArgonEvent
    {
        ForgetChangedPin(ev);
        return appHubServer.BroadcastChannel(ev, SpaceId, this.GetPrimaryKey(), ct);
    }

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Matches MessageDeduplicationService's entry lifetime. Anything a previous activation wrote
        // has expired by then, so from that moment this activation's own memory is the whole answer.
        _self = await Get();

        // A migrated activation arrives holding everything it had on the other silo — the roster in
        // persisted state, the rest in volatile state — because Orleans carries an IPersistentState
        // across a move and skips the read on the far side. Reading storage back over it and clearing
        // it is exactly what the move exists to avoid.
        //
        // A fresh activation still resets: voice membership is activation-scoped by design, so a silo
        // that died takes its call with it rather than leaving ghosts behind.
        if (!activation.State.Activated)
        {
            // Matches MessageDeduplicationService's entry lifetime. Anything a previous activation
            // wrote has expired by then, so from that moment this activation's own memory is the
            // whole answer.
            activation.State.DedupTrustedUntil = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);

            await state.ReadStateAsync(cancellationToken);

            state.State.Users.Clear();
            state.State.UserJoinTimes.Clear();
            state.State.LastMembershipChange = DateTimeOffset.UtcNow;
            state.State.EgressActive = false;

            await state.WriteStateAsync(cancellationToken);
        }

        activation.State.Activated = true;

        // One timer carries both flushes rather than two on their own schedules. They are the same
        // kind of debt — a durable copy of something this activation already holds authoritatively —
        // and both are correct while at most one interval behind, so a second timer would only
        // double the wakeups every channel activation in the cluster costs. Three seconds is well
        // inside what an unread badge or a reaction count may lag by without anyone noticing.
        _reactionFlushTimer = this.RegisterGrainTimer(
            // The mark goes first because it is the one that cannot throw: a reaction flush that
            // fails on a bad row would otherwise take the high-water mark down with it every tick,
            // and that debt only grows.
            async _ =>
            {
                await FlushLastMessageIdAsync();
                await FlushReactionsAsync();
            },
            new GrainTimerCreationOptions(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));

        // Timers do not travel, only the fact that these bots were typing. Empty on a fresh
        // activation, so this is a no-op there.
        foreach (var userId in activation.State.BotTyping.ToArray())
            ArmBotTypingStop(userId);
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Unlike the roster below, this one runs on migration too, and must. The successor activation
        // starts with an empty mark — it is not carried in ChannelActivationState — so every id noted
        // since the last timer tick exists nowhere but in this object. Skipping the migrating case
        // would leave the channel's stored mark up to one interval behind with nothing to correct it
        // until the next message happens to arrive, and every member's unread badge for the channel
        // wrong until then.
        //
        // Ahead of the reaction flush for the same reason the timer orders them this way: this one
        // swallows its own failures, that one can throw and end the shutdown path early.
        await FlushLastMessageIdAsync();

        // Drain the cache publishes as well. Orleans starts the successor activation only once this
        // returns, so a write still in flight here is the one case where an older id could land after
        // a newer one from a different activation — and nothing would rewrite the cell until the
        // channel saw another message.
        //
        // Bounded, because the tail is one serialized chain of Redis writes and a Redis that times out
        // rather than refuses makes it arbitrarily long: a channel that took a hundred messages in the
        // seconds before this would hold the shutdown for minutes, ahead of the reaction flush, the XP
        // settle and the successor's first turn. That is the drain path k8s gives a fixed grace period,
        // so an unbounded wait here turns an orderly stop into a kill.
        //
        // Giving up costs the ordering guarantee above for a channel whose Redis is already broken,
        // and the successor's first send rewrites the cell anyway. The durable row is already flushed.
        try
        {
            await lastMessagePublishTail.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Channel {ChannelId} left a last-message publish in flight; the cell is repaired by the next send",
                this.GetPrimaryKey());
        }

        // Flush pending reactions before shutdown
        await FlushReactionsAsync();

        // Settle XP for all users still in channel. Runs on migration too: it awards the period that
        // has actually elapsed and rebases the clock, so the accounting is continuous across the move.
        await SettleXpForAllUsersAsync();

        // Migration is not a departure. Leaving everyone here would broadcast LeavedFromChannelUser to
        // every client and tell the space the call emptied — a visible mass disconnect caused by the
        // cluster rebalancing itself. The roster travels with the activation instead.
        if (reason.ReasonCode == DeactivationReasonCode.Migrating)
            return;

        await Task.WhenAll(state.State.Users.Select(x => Leave(x.Key)));
    }

    public Task<List<RealtimeChannelUser>> GetMembers()
        => Task.FromResult(state.State.Users.Select(x => x.Value).ToList());

    public Task<ChannelRealtimeState> GetRealtimeStateAsync(CancellationToken ct = default)
        => Task.FromResult(new ChannelRealtimeState(state.State.Users.Select(x => x.Value).ToList()));

    [OneWay]
    public Task ClearChannel()
    {
        GrainContext.Deactivate(new DeactivationReason(DeactivationReasonCode.None, ""));
        return Task.CompletedTask;
    }

    [OneWay]
    public async ValueTask OnTypingEmit()
    {
        // Unchecked on purpose: a purely visual event, frequent, and seen only by the channel's viewers.
        ChannelGrainInstrument.TypingEvents.Add(1,
            new KeyValuePair<string, object?>("event_type", "typing"));
        
        await FireChannel(new UserTypingEvent(SpaceId, ChannelId.ShardId, this.GetUserId(), null));
    }

    [OneWay]
    public async ValueTask OnTypingStopEmit()
    {
        ChannelGrainInstrument.TypingEvents.Add(1,
            new KeyValuePair<string, object?>("event_type", "stop_typing"));
        
        await FireChannel(new UserStopTypingEvent(SpaceId, ChannelId.ShardId, this.GetUserId()));
    }

    private static readonly TimeSpan BotTypingTimeout = TimeSpan.FromSeconds(8);

    [OneWay]
    public async ValueTask OnBotTypingEmit(TypingKind kind)
    {
        var userId    = this.GetUserId();
        var channelId = ChannelId.ShardId;

        // Bots are outside code and the bot API's Permission() is declarative, so this is the only gate.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), userId, ArgonEntitlement.SendMessages))
            return;

        ChannelGrainInstrument.TypingEvents.Add(1,
            new KeyValuePair<string, object?>("event_type", "bot_typing"));

        // Cancel existing auto-stop timer for this user if any
        if (_botTypingTimers.Remove(userId, out var existing))
            existing.Dispose();

        await FireChannel(new UserTypingEvent(SpaceId, channelId, userId, kind));

        // Register auto-stop timer — fires UserStopTypingEvent after timeout
        ArmBotTypingStop(userId);
    }

    /// <summary>Fires the stop-typing event once the bot has gone quiet for <see cref="BotTypingTimeout"/>.</summary>
    /// <remarks>
    /// Separate from the call that starts it because a migrated activation has to arm it again: the
    /// timer does not travel, and without it a bot that stopped typing on the old silo would show as
    /// typing forever on the new one.
    /// </remarks>
    private void ArmBotTypingStop(Guid userId)
    {
        activation.State.BotTyping.Add(userId);

        _botTypingTimers[userId] = this.RegisterGrainTimer(async _ =>
        {
            _botTypingTimers.Remove(userId);
            activation.State.BotTyping.Remove(userId);
            await FireChannel(new UserStopTypingEvent(SpaceId, ChannelId.ShardId, userId));
        }, new GrainTimerCreationOptions(BotTypingTimeout, Timeout.InfiniteTimeSpan));
    }

    public async Task<bool> KickMemberFromChannel(Guid memberId)
    {
        if (_self.ChannelType != ChannelType.Voice)
        {
            ChannelGrainInstrument.MemberKicks.Add(1,
                new KeyValuePair<string, object?>("result", "invalid_channel"));
            return false;
        }

        await using var ctx = await context.CreateDbContextAsync();

        var userId = this.GetUserId();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), userId, ArgonEntitlement.KickMember))
        {
            ChannelGrainInstrument.MemberKicks.Add(1,
                new KeyValuePair<string, object?>("result", "no_permission"));
            return false;
        }

        if (!state.State.Users.ContainsKey(memberId))
        {
            ChannelGrainInstrument.MemberKicks.Add(1,
                new KeyValuePair<string, object?>("result", "not_in_channel"));
            return false;
        }

        await VoiceControl.KickParticipantAsync(new ArgonUserId(memberId), ChannelId);
        // The webhook is not configured everywhere, so the roster cannot wait for participant_left.
        await Leave(memberId);

        ChannelGrainInstrument.MemberKicks.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        return true;
    }

    public async Task<IMoveVoiceMemberResult> MoveVoiceMember(Guid memberId, Guid targetChannelId)
    {
        var error = await TryMoveVoiceMemberAsync(memberId, targetChannelId);

        ChannelGrainInstrument.MemberMoves.Add(1,
            new KeyValuePair<string, object?>("result", error.ToString().ToLowerInvariant()));

        return error == MoveVoiceMemberError.NONE
            ? new SuccessMoveVoiceMember()
            : new FailedMoveVoiceMember(error);
    }

    private async Task<MoveVoiceMemberError> TryMoveVoiceMemberAsync(Guid memberId, Guid targetChannelId)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (targetChannelId == channelId)
            return MoveVoiceMemberError.SAME_CHANNEL;
        if (!state.State.Users.ContainsKey(memberId))
            return MoveVoiceMemberError.MEMBER_NOT_IN_CHANNEL;
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.MoveMember))
            return MoveVoiceMemberError.INSUFFICIENT_PERMISSIONS;

        await using var ctx = await context.CreateDbContextAsync();
        var targetType = await ctx.Channels.AsNoTracking()
           .Where(c => c.Id == targetChannelId && c.SpaceId == SpaceId && !c.IsDeleted)
           .Select(c => (ChannelType?)c.ChannelType)
           .FirstOrDefaultAsync();

        if (targetType is null)
            return MoveVoiceMemberError.TARGET_NOT_FOUND;
        if (targetType != ChannelType.Voice)
            return MoveVoiceMemberError.TARGET_IS_NOT_VOICE;
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, targetChannelId, callerId, ArgonEntitlement.MoveMember))
            return MoveVoiceMemberError.INSUFFICIENT_PERMISSIONS;
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, targetChannelId, memberId, ArgonEntitlement.Connect))
            return MoveVoiceMemberError.MEMBER_CANNOT_JOIN_TARGET;
        if (!callKit.Value.Sfu.Has(SfuInstanceCfg.MoveCapability))
            return MoveVoiceMemberError.SFU_UNAVAILABLE;

        var member     = new ArgonUserId(memberId);
        var target     = this.GrainFactory.GetGrain<IChannelGrain>(targetChannelId);
        var targetRoom = new ArgonRoomId(SpaceId, targetChannelId);

        // Rosters and the slot first, so the webhooks the move fires find the member already where it went.
        var admission = await target.AdmitMovedMemberAsync(memberId);
        await LeaveAsync(memberId, "move");

        if (!await VoiceControl.MoveParticipantAsync(member, ChannelId, targetRoom))
        {
            // Which room the SFU left them in is unknown, so they end up in neither: out of voice.
            await VoiceControl.KickParticipantAsync(member, ChannelId);
            await VoiceControl.KickParticipantAsync(member, targetRoom);
            await target.ReleaseMember(memberId);
            return MoveVoiceMemberError.SFU_UNAVAILABLE;
        }

        // The move keeps the source room's permissions; these are the target's.
        await VoiceControl.UpdateParticipantRightsAsync(member, targetRoom, admission.Rights);

        return MoveVoiceMemberError.NONE;
    }

    public async Task<VoiceAdmission> AdmitMovedMemberAsync(Guid userId)
    {
        // A stale entry here (the rosters disagreed) is replaced, as Join replaces one.
        await LeaveAsync(userId, "move");

        var (muted, deafened) = await ReadVoiceRestrictionAsync(userId);
        await AdmitAsync(userId, ServerFlagsFor(muted, deafened), "move");

        return new VoiceAdmission(await MediaRightsAsync(userId, muted, deafened));
    }

    public async Task UpdateVoiceState(ChannelMemberState requested)
    {
        if (!state.State.Users.TryGetValue(this.GetUserId(), out var member))
            return;

        await SetMemberStateAsync(member, (member.state & ServerVoiceFlags) | (requested & SelfVoiceFlags));
    }

    public async Task ApplyVoiceRestriction(Guid memberId, bool muted, bool deafened)
    {
        if (!state.State.Users.TryGetValue(memberId, out var member))
            return;

        await SetMemberStateAsync(member, (member.state & SelfVoiceFlags) | ServerFlagsFor(muted, deafened));
        await VoiceControl.UpdateParticipantRightsAsync(new ArgonUserId(memberId), ChannelId,
            await MediaRightsAsync(memberId, muted, deafened));

        if (_self.Broadcast is not null)
            await Radio.ApplyVoiceRestrictionAsync(memberId, muted, deafened);
    }

    public Task ReleaseMember(Guid userId)
        => Leave(userId);

    [OneWay]
    public async Task TeardownVoiceAsync()
    {
        foreach (var userId in state.State.Users.Keys.ToList())
        {
            await VoiceControl.KickParticipantAsync(new ArgonUserId(userId), ChannelId);
            await Leave(userId);
        }
    }

    private const ChannelMemberState SelfVoiceFlags =
        ChannelMemberState.MUTED | ChannelMemberState.MUTED_HEADPHONES | ChannelMemberState.STREAMING;

    private const ChannelMemberState ServerVoiceFlags =
        ChannelMemberState.MUTED_BY_SERVER | ChannelMemberState.MUTED_HEADPHONES_BY_SERVER;

    private static ChannelMemberState ServerFlagsFor(bool muted, bool deafened)
        => (muted ? ChannelMemberState.MUTED_BY_SERVER : ChannelMemberState.NONE)
         | (deafened ? ChannelMemberState.MUTED_HEADPHONES_BY_SERVER : ChannelMemberState.NONE);

    private async Task SetMemberStateAsync(RealtimeChannelUser member, ChannelMemberState next)
    {
        if (member.state == next)
            return;

        state.State.Users[member.userId] = member with { state = next };
        await Fire(new VoiceMemberStateChanged(SpaceId, this.GetPrimaryKey(), member.userId, next));
    }

    private async Task<(bool Muted, bool Deafened)> ReadVoiceRestrictionAsync(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();
        var member = await ctx.UsersToServerRelations.AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.UserId == userId && !m.IsDeleted)
           .Select(m => new { m.IsVoiceMuted, m.IsVoiceDeafened })
           .FirstOrDefaultAsync();

        return (member?.IsVoiceMuted ?? false, member?.IsVoiceDeafened ?? false);
    }

    private async Task<SfuMediaRights> MediaRightsAsync(Guid userId, bool muted, bool deafened)
    {
        var channelId = this.GetPrimaryKey();
        var rights    = deafened ? SfuMediaRights.None : SfuMediaRights.Listen;

        if (!muted && !deafened
         && await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, userId, ArgonEntitlement.Speak))
            rights |= SfuMediaRights.Microphone;
        if (await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, userId, ArgonEntitlement.Video))
            rights |= SfuMediaRights.Camera;
        if (await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, userId, ArgonEntitlement.Stream))
            rights |= SfuMediaRights.ScreenShare;

        return rights;
    }

    /// <summary>A member is in one voice channel per space: joining here drops them from the last one.</summary>
    private async Task LeavePreviousVoiceChannelAsync(Guid userId)
    {
        var slot = await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).GetUserVoiceSlotAsync(userId);
        if (slot is null || slot.ChannelId == this.GetPrimaryKey())
            return;

        // One-way: two members swapping channels at once would otherwise wait on each other's grain.
        await this.GrainFactory.GetGrain<IChannelGrain>(slot.ChannelId).ReleaseMember(userId);
    }

    private IVoiceControlGrain VoiceControl => this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty);

    private IVoiceBroadcastGrain Radio => this.GrainFactory.GetGrain<IVoiceBroadcastGrain>(this.GetPrimaryKey());

    public async Task<bool> BeginRecord(CancellationToken ct = default)
    {
        if (state.State.EgressActive)
        {
            ChannelGrainInstrument.RecordingsStarted.Add(1,
                new KeyValuePair<string, object?>("result", "already_active"));
            return false;
        }

        var result = await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty)
           .BeginRecordAsync(new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()), ct);

        await Fire(new RecordStarted(this.SpaceId, this.GetPrimaryKey(), this.GetUserId()), ct);

        state.State.EgressActive      = true;
        state.State.EgressId          = result;
        state.State.UserCreatedEgress = this.GetUserId();

        ChannelGrainInstrument.RecordingsStarted.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        return true;
    }

    public async Task<bool> StopRecord(CancellationToken ct = default)
    {
        if (!state.State.EgressActive)
        {
            ChannelGrainInstrument.RecordingsStopped.Add(1,
                new KeyValuePair<string, object?>("result", "not_active"));
            return false;
        }
        
        var egressId = state.State.EgressId;
        await Fire(new RecordEnded(this.SpaceId, this.GetPrimaryKey()), ct);
        state.State.EgressActive      = false;
        state.State.EgressId          = null;
        state.State.UserCreatedEgress = null;
        var result = await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty)
           .StopRecordAsync(new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()), egressId!, ct);

        ChannelGrainInstrument.RecordingsStopped.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        return result;
    }

    public async Task<Either<string, JoinToChannelError>> Join()
    {
        if (_self.ChannelType != ChannelType.Voice)
            return JoinToChannelError.CHANNEL_IS_NOT_VOICE;

        var userId = this.GetUserId();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), userId, ArgonEntitlement.Connect))
            return JoinToChannelError.INSUFFICIENT_PERMISSIONS;

        if (state.State.UserJoinTimes.TryGetValue(userId, out _))
        {
            await SettleXpForAllUsersAsync();
            state.State.UserJoinTimes.Remove(userId);
            state.State.Users.Remove(userId);
            await Fire(new LeavedFromChannelUser(SpaceId, this.GetPrimaryKey(), userId));
            await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserLeftVoiceAsync(userId, this.GetPrimaryKey());
        }
        else
            await LeavePreviousVoiceChannelAsync(userId);

        var (muted, deafened) = await ReadVoiceRestrictionAsync(userId);
        await AdmitAsync(userId, ServerFlagsFor(muted, deafened), "direct");

        // Track call joined for stats
        _ = TrackCallJoinedAsync(userId);

        return await VoiceControl.IssueAuthorizationTokenAsync(new ArgonUserId(userId), ChannelId, SfuPermissionKind.DefaultUser,
            await MediaRightsAsync(userId, muted, deafened));
    }

    /// <summary>Puts a member on the roster: the events, the space's voice slot, the radio and the activation lease.</summary>
    private async Task AdmitAsync(Guid userId, ChannelMemberState flags, string source)
    {
        // Settle XP for existing users before adding the new one
        await SettleXpForAllUsersAsync();

        state.State.Users.Add(userId, new RealtimeChannelUser(userId, flags));
        state.State.UserJoinTimes[userId] = DateTimeOffset.UtcNow;
        await state.WriteStateAsync();

        await Fire(new JoinedToChannelUser(SpaceId, this.GetPrimaryKey(), userId));
        if (flags != ChannelMemberState.NONE)
            await Fire(new VoiceMemberStateChanged(SpaceId, this.GetPrimaryKey(), userId, flags));
        await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserJoinedVoiceAsync(userId, this.GetPrimaryKey(), DateTimeOffset.UtcNow);
        if (_self.Broadcast is not null)
            await Radio.OnMemberJoinedAsync(userId);

        this.DelayDeactivation(TimeSpan.FromDays(1));

        ChannelGrainInstrument.VoiceJoins.Add(1,
            new KeyValuePair<string, object?>("source", source));

        ChannelGrainInstrument.VoiceActiveUsers.Record(state.State.Users.Count);
    }

    public async Task<Either<DrawingSessionDescriptor, DrawingDenyKind>> StartDrawingSession()
    {
        if (_self.ChannelType != ChannelType.Voice)
            return DrawingDenyKind.NotStreaming;

        var streamerId = this.GetUserId();

        // The caller must currently be in the voice channel (i.e. actually able to share).
        if (!state.State.Users.ContainsKey(streamerId))
            return DrawingDenyKind.NotStreaming;

        // Feature flag gate (evaluated for the streamer).
        var ff = await this.GrainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty)
           .EvaluateAsync("af.screencast.drawing", FeatureFlagEvaluationContext.ForUser(streamerId));
        if (!ff.IsEnabled)
            return DrawingDenyKind.FeatureDisabled;

        // Compute the allowed-drawers set: members passing BOTH the channel CanDrawOnStream
        // entitlement AND the streamer's "stream.draw" privacy rule.
        var privacy = this.GrainFactory.GetGrain<IPrivacyPolicyGrain>(streamerId);
        var allowed = new List<Guid>();
        foreach (var memberId in state.State.Users.Keys.ToList())
        {
            if (memberId == streamerId) continue; // streamer annotates their own surface client-side

            var hasEntitlement = await entitlementChecker.HasChannelAccessAsync(
                SpaceId, this.GetPrimaryKey(), memberId, ArgonEntitlement.CanDrawOnStream);
            if (!hasEntitlement) continue;

            var privacyOk = await privacy.EvaluateAsync(memberId, PrivacyKeys.StreamDraw, SpaceId);
            if (!privacyOk) continue;

            allowed.Add(memberId);
        }

        var sessionId = ArgonId.New().ToString("N");
        activation.State.DrawingSession = new DrawingSessionState(sessionId, streamerId, allowed.ToHashSet());

        await Fire(new DrawingSessionStarted(
            SpaceId, this.GetPrimaryKey(), sessionId, streamerId,
            new IonArray<Guid>(allowed), DrawingDefaultTtlMs));

        return new DrawingSessionDescriptor(sessionId, streamerId, allowed, DrawingDefaultTtlMs);
    }

    public async Task<bool> StopDrawingSession(string sessionId)
    {
        if (activation.State.DrawingSession is not { } session) return false;
        if (session.SessionId != sessionId) return false;
        if (session.StreamerId != this.GetUserId()) return false; // only the streamer may close

        activation.State.DrawingSession = null;
        await Fire(new DrawingSessionEnded(SpaceId, this.GetPrimaryKey(), sessionId));
        return true;
    }

    public Task Leave(Guid userId)
        => LeaveAsync(userId, "direct");

    private async Task LeaveAsync(Guid userId, string source)
    {
        if (!state.State.Users.ContainsKey(userId))
            return;

        // Settle XP for ALL users (including the one leaving) before removing
        await SettleXpForAllUsersAsync();

        // Only record total session duration for metrics
        if (state.State.UserJoinTimes.TryGetValue(userId, out var joinTime))
        {
            var duration = DateTimeOffset.UtcNow - joinTime;
            ChannelGrainInstrument.VoiceSessionDuration.Record(duration.TotalSeconds);
            state.State.UserJoinTimes.Remove(userId);
        }

        state.State.Users.Remove(userId);
        await Fire(new LeavedFromChannelUser(SpaceId, this.GetPrimaryKey(), userId));
        // A no-op when the slot already points elsewhere, as it does for a member a move took away.
        await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserLeftVoiceAsync(userId, this.GetPrimaryKey());
        await state.WriteStateAsync();

        if (_self.Broadcast is not null)
            await Radio.OnMemberLeftAsync(userId);

        // End the streamer's drawing session if they left the channel.
        if (activation.State.DrawingSession is { } ds && ds.StreamerId == userId)
        {
            var sessionId = ds.SessionId;
            activation.State.DrawingSession = null;
            await Fire(new DrawingSessionEnded(SpaceId, this.GetPrimaryKey(), sessionId));
        }

        if (state.State.Users.Count == 0)
            this.DelayDeactivation(TimeSpan.MinValue);

        ChannelGrainInstrument.VoiceLeaves.Add(1,
            new KeyValuePair<string, object?>("source", source));

        ChannelGrainInstrument.VoiceActiveUsers.Record(state.State.Users.Count);
    }

    public async Task OnParticipantJoined(Guid userId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return;

        // Already here through Join or a move: the webhook only fills in what neither did.
        if (state.State.Users.ContainsKey(userId))
            return;

        var (muted, deafened) = await ReadVoiceRestrictionAsync(userId);
        await AdmitAsync(userId, ServerFlagsFor(muted, deafened), "webhook");
    }

    public async Task<Either<ChannelEntity, UpdateChannelError>> UpdateChannelSettings(string? name, string? description, int? slowModeSeconds,
        int? bitrate, CancellationToken ct = default)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageChannels, ct))
            return UpdateChannelError.INSUFFICIENT_PERMISSIONS;

        // Validate before opening the context: a rejected request should not have touched the DB.
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length == 0)
                return UpdateChannelError.NAME_EMPTY;
            if (name.Length > 128)
                return UpdateChannelError.NAME_TOO_LONG;
        }

        if (description is { Length: > 1024 })
            return UpdateChannelError.DESCRIPTION_TOO_LONG;

        if (slowModeSeconds is { } seconds)
        {
            if (_self.ChannelType != ChannelType.Text)
                return UpdateChannelError.NOT_A_TEXT_CHANNEL;
            if (!ChannelEntity.AllowedSlowModeSeconds.Contains(seconds))
                return UpdateChannelError.SLOW_MODE_NOT_ALLOWED;
        }

        if (bitrate is { } kbps)
        {
            if (_self.ChannelType != ChannelType.Voice)
                return UpdateChannelError.NOT_A_VOICE_CHANNEL;
            // 0 clears back to the default; anything else has to sit inside the range.
            if (kbps != 0 && (kbps < ChannelEntity.MinBitrateKbps || kbps > ChannelEntity.MaxBitrateKbps))
                return UpdateChannelError.BITRATE_OUT_OF_RANGE;
        }

        await using var ctx = await context.CreateDbContextAsync(ct);

        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null)
            return UpdateChannelError.CHANNEL_NOT_FOUND;

        // Only what changed travels: a patch carrying the whole channel would race a concurrent
        // reorder, which has its own event.
        var patch = new IonPartial<ArgonChannel>();

        if (name is not null && name != channel.Name)
        {
            channel.Name = name;
            patch.Modify(x => x.name, name);
        }

        if (description is not null && description != channel.Description)
        {
            channel.Description = description;
            patch.Modify(x => x.description, description);
        }

        if (slowModeSeconds is { } window)
        {
            var value = window == 0 ? null : (TimeSpan?)TimeSpan.FromSeconds(window);
            if (value != channel.SlowMode)
            {
                channel.SlowMode = value;
                if (value is null)
                    patch.Remove(x => x.slowModeSeconds);
                else
                    patch.Modify(x => x.slowModeSeconds, window);
            }
        }

        if (bitrate is { } rate)
        {
            var value = rate == 0 ? (int?)null : rate;
            if (value != channel.Bitrate)
            {
                channel.Bitrate = value;
                if (value is null)
                    patch.Remove(x => x.bitrate);
                else
                    patch.Modify(x => x.bitrate, value);
            }
        }

        if (patch.Count == 0)
            return await WithStoredMarkAsync(ctx, channel, ct);

        await ctx.SaveChangesAsync(ct);

        // Refresh the activation's copy: SendMessage reads the cooldown off _self on every send, and
        // the write went through a detached context that the activation cannot see.
        _self = channel;

        await readCache.SignalInvalidationAsync(SpaceId, ct: ct);
        await Fire(new ChannelModifiedV2(SpaceId, channelId, patch), ct);

        return await WithStoredMarkAsync(ctx, channel, ct);
    }

    public async Task<Either<ChannelEntity, UpdateChannelError>> SetChannelType(ChannelType type, CancellationToken ct = default)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        // It rewrites who may post, so it takes what editing the channel's overwrites takes.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageChannels, ct)
         || !await entitlementChecker.HasAccessAsync(SpaceId, callerId, ArgonEntitlement.ManageChannels | ArgonEntitlement.ManageArchetype, ct))
            return UpdateChannelError.INSUFFICIENT_PERMISSIONS;

        if (_self.ChannelType is not (ChannelType.Text or ChannelType.Announcement)
         || type is not (ChannelType.Text or ChannelType.Announcement))
            return UpdateChannelError.TYPE_NOT_CONVERTIBLE;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null)
            return UpdateChannelError.CHANNEL_NOT_FOUND;

        if (channel.ChannelType == type)
            return await WithStoredMarkAsync(ctx, channel, ct);

        var everyoneId = await ctx.Archetypes
           .Where(a => a.SpaceId == SpaceId && a.IsDefault)
           .Select(a => a.Id)
           .FirstOrDefaultAsync(ct);
        var everyone = await ctx.ChannelEntitlementOverwrites
           .FirstOrDefaultAsync(o => o.ChannelId == channelId && o.Scope == IArchetypeScope.Archetype && o.ArchetypeId == everyoneId, ct);

        var patch = new IonPartial<ArgonChannel>();
        channel.ChannelType = type;
        patch.Modify(x => x.type, type);
        PatchAnnouncementForType(patch, channel);

        var announcement = ChannelAnnouncement.Of(channel);

        if (type == ChannelType.Announcement)
        {
            // A deny the owner set before stays theirs; only one added here is lifted on the way back.
            var added = false;

            if (everyone is not null)
            {
                added = !everyone.Deny.HasFlag(ArgonEntitlement.SendMessages);
                everyone.Allow &= ~ArgonEntitlement.SendMessages;
                everyone.Deny  |= ArgonEntitlement.SendMessages;
            }
            else if (everyoneId != Guid.Empty)
            {
                ctx.ChannelEntitlementOverwrites.Add(new ChannelEntitlementOverwriteEntity
                {
                    ChannelId   = channelId,
                    ArchetypeId = everyoneId,
                    Scope       = IArchetypeScope.Archetype,
                    Allow       = ArgonEntitlement.None,
                    Deny        = ArgonEntitlement.SendMessages,
                    CreatorId   = callerId
                });
                added = true;
            }

            channel.Announcement = announcement with { AddedSendDeny = added };

            if (channel.SlowMode is not null)
            {
                channel.SlowMode = null;
                patch.Remove(x => x.slowModeSeconds);
            }
        }
        else
        {
            if (announcement.AddedSendDeny && everyone is not null && everyone.Deny.HasFlag(ArgonEntitlement.SendMessages))
            {
                everyone.Deny &= ~ArgonEntitlement.SendMessages;
                if (everyone is { Allow: ArgonEntitlement.None, Deny: ArgonEntitlement.None })
                    ctx.ChannelEntitlementOverwrites.Remove(everyone);
            }

            if (announcement.AddedSendDeny)
                channel.Announcement = announcement with { AddedSendDeny = false };
        }

        await ctx.SaveChangesAsync(ct);

        _self = channel;

        if (type == ChannelType.Text)
            await ForgetFollowersAsync(ctx, ct);

        await readCache.SignalInvalidationAsync(SpaceId, ct: ct);
        await Fire(new ChannelModifiedV2(SpaceId, channelId, patch), ct);
        await Fire(new EntitlementsChanged(SpaceId, null), ct);
        if (type == ChannelType.Text)
            await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).ForgetMainAnnouncementChannelAsync(channelId);

        return await WithStoredMarkAsync(ctx, channel, ct);
    }

    /// <summary>
    /// The channel as the caller should get it back: metadata from its row, high-water mark from
    /// where the high-water mark actually lives.
    /// </summary>
    /// <remarks>
    /// <para><c>ChannelEntity.LastMessageId</c> is the dead column — nothing has written it since the
    /// counter moved to <c>ChannelLastMessages</c> — so a channel loaded straight out of
    /// <c>Channels</c> carries a number frozen at the moment of that split, and
    /// <c>ChannelInteractionImpl</c> maps this entity onto the wire as <c>SuccessUpdateChannel</c>
    /// with the field on it. A client that merges the record it gets back from a rename would
    /// overwrite its unread state for that channel with a value from before the split. Every other
    /// path that serves an <c>ArgonChannel</c> replaces the field already; this is the one that has
    /// to do it here.</para>
    ///
    /// <para><b>A copy, not an assignment onto the entity.</b> The context that loaded it is still
    /// open and the entity is tracked, so writing the property would leave EF holding a modification
    /// this method has no intention of saving — harmless today only because nothing saves again
    /// afterwards, which is not a property worth depending on.</para>
    ///
    /// <para><b>Not the in-memory mark.</b> <see cref="lastMessage"/> starts empty on a fresh
    /// activation, so a rename after a silo restart would answer zero for a channel with ten thousand
    /// messages in it. The stored row is the one thing that is right regardless of activation age.
    /// The Redis cell is not consulted either: it would be at most one flush interval fresher, on a
    /// path that is a rename rather than a read of unread state, and the client has three better
    /// sources for the counter.</para>
    /// </remarks>
    private static async Task<ChannelEntity> WithStoredMarkAsync(
        ApplicationDbContext ctx, ChannelEntity channel, CancellationToken ct)
        => channel with
        {
            LastMessageId = await ctx.ChannelLastMessages
               .AsNoTracking()
               .Where(m => m.ChannelId == channel.Id)
               .Select(m => m.LastMessageId)
               .FirstOrDefaultAsync(ct),
            // Overwrites the context tracked do not survive the trip across a grain boundary.
            EntitlementOverwrites = new List<ChannelEntitlementOverwriteEntity>()
        };

    public async Task<ISetBroadcastSettingsResult> SetBroadcastMode(bool enabled)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageChannels))
            return new FailedSetBroadcastSettings(SetBroadcastSettingsError.INSUFFICIENT_PERMISSIONS);

        await using var ctx = await context.CreateDbContextAsync();
        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        // A deleted channel is nobody's voice channel either.
        if (channel is null || channel.ChannelType != ChannelType.Voice)
            return new FailedSetBroadcastSettings(SetBroadcastSettingsError.CHANNEL_IS_NOT_VOICE);

        var on = channel.Broadcast is not null;
        if (enabled == on)
            return await BroadcastResultAsync(ctx, channel);

        channel.Broadcast = enabled ? ChannelBroadcast.Default() : null;
        await ctx.SaveChangesAsync();

        if (enabled)
        {
            // Decision 3: everyone who can join HQ transmits by default, as a visible overwrite an admin can narrow.
            await GrantBroadcastToEveryoneAsync(ctx, channelId, callerId);
            // Decision 8: a broadcast channel is nobody's target. One-way, like the delete path.
            await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).UntargetChannelAsync(channelId);
        }

        await AnnounceBroadcastChangedAsync(channel);
        return await BroadcastResultAsync(ctx, channel);
    }

    public async Task<ISetBroadcastSettingsResult> PatchBroadcastSettings(IonPartial<BroadcastSettings> patch)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageChannels))
            return new FailedSetBroadcastSettings(SetBroadcastSettingsError.INSUFFICIENT_PERMISSIONS);

        await using var ctx = await context.CreateDbContextAsync();
        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        // A deleted channel is nobody's voice channel either.
        if (channel is null || channel.ChannelType != ChannelType.Voice)
            return new FailedSetBroadcastSettings(SetBroadcastSettingsError.CHANNEL_IS_NOT_VOICE);
        if (channel.Broadcast is not { } current)
            return new FailedSetBroadcastSettings(SetBroadcastSettingsError.NOT_A_BROADCAST_CHANNEL);

        var (next, selfTargeted) = current.Apply(patch, channelId);
        if (selfTargeted || !next.Targets.SequenceEqual(current.Targets) && !await TargetsQualifyAsync(ctx, next.Targets))
            return new FailedSetBroadcastSettings(SetBroadcastSettingsError.INVALID_TARGET);
        if (next.SameAs(current))
            return await BroadcastResultAsync(ctx, channel);

        channel.Broadcast = next;
        await ctx.SaveChangesAsync();
        await AnnounceBroadcastChangedAsync(channel);
        return await BroadcastResultAsync(ctx, channel);
    }

    public async Task RemoveBroadcastTarget(Guid targetChannelId)
    {
        await using var ctx = await context.CreateDbContextAsync();
        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == this.GetPrimaryKey());
        if (channel?.Broadcast is not { } current || !current.Targets.Contains(targetChannelId))
            return;

        channel.Broadcast = current with { Targets = current.Targets.Where(t => t != targetChannelId).ToList() };
        await ctx.SaveChangesAsync();
        await AnnounceBroadcastChangedAsync(channel);
    }

    /// <summary>After a write of the broadcast column: the activation's copy, the read cache, the clients and the radio.</summary>
    private async Task AnnounceBroadcastChangedAsync(ChannelEntity channel)
    {
        _self = channel;

        var patch = new IonPartial<ArgonChannel>();
        if (channel.Broadcast is { } broadcast)
            patch.Modify(x => x.broadcast, ChannelBroadcast.ToDto(broadcast));
        else
            patch.Remove(x => x.broadcast);

        await readCache.SignalInvalidationAsync(SpaceId);
        await Fire(new ChannelModifiedV2(SpaceId, channel.Id, patch));
        await Radio.OnSettingsChangedAsync();
    }

    private static async Task<ISetBroadcastSettingsResult> BroadcastResultAsync(ApplicationDbContext ctx, ChannelEntity channel)
        => new SuccessSetBroadcastSettings((await WithStoredMarkAsync(ctx, channel, CancellationToken.None)).ToDto());

    /// <summary>Voice channels of this space that do not broadcast themselves (decision 8); the channel itself is refused by the merge.</summary>
    private async Task<bool> TargetsQualifyAsync(ApplicationDbContext ctx, List<Guid> targets)
    {
        if (targets.Count == 0)
            return true;

        var valid = await ctx.Channels.AsNoTracking()
           .CountAsync(c => targets.Contains(c.Id) && c.SpaceId == SpaceId && !c.IsDeleted
                         && c.ChannelType == ChannelType.Voice && c.Broadcast == null);
        return valid == targets.Count;
    }

    private async Task GrantBroadcastToEveryoneAsync(ApplicationDbContext ctx, Guid channelId, Guid callerId)
    {
        var overwrites = await ctx.ChannelEntitlementOverwrites.Where(o => o.ChannelId == channelId).ToListAsync();
        if (overwrites.Any(o => o.Allow.HasFlag(ArgonEntitlement.Broadcast) || o.Deny.HasFlag(ArgonEntitlement.Broadcast)))
            return;

        var everyoneId = await ctx.Archetypes
           .Where(a => a.SpaceId == SpaceId && a.IsDefault)
           .Select(a => a.Id)
           .FirstOrDefaultAsync();
        if (everyoneId == Guid.Empty)
            return;

        var everyone = overwrites.FirstOrDefault(o => o.Scope == IArchetypeScope.Archetype && o.ArchetypeId == everyoneId);
        if (everyone is null)
            ctx.ChannelEntitlementOverwrites.Add(new ChannelEntitlementOverwriteEntity
            {
                ChannelId   = channelId,
                ArchetypeId = everyoneId,
                Scope       = IArchetypeScope.Archetype,
                Allow       = ArgonEntitlement.Broadcast,
                Deny        = ArgonEntitlement.None,
                CreatorId   = callerId
            });
        else
            everyone.Allow |= ArgonEntitlement.Broadcast;

        await ctx.SaveChangesAsync();

        await permissionCache.SignalSpaceInvalidationAsync(SpaceId);
        await Fire(new EntitlementsChanged(SpaceId, null));
    }

    public async Task<DeleteMessageError> DeleteMessage(long messageId, CancellationToken ct = default)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var message = await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .Select(m => new { m.CreatorId, IsCrosspost = m.Crosspost != null })
           .FirstOrDefaultAsync(ct);

        if (message is null)
            return DeleteMessageError.MESSAGE_NOT_FOUND;

        // Retracting your own words needs only a seat in the channel, which a banned or departed author
        // no longer has; taking down somebody else's is moderation. A crossposted copy belongs to the
        // channel it landed in, not to the source's author.
        var own = message.CreatorId == callerId && !message.IsCrosspost;
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId,
                own ? ArgonEntitlement.ViewChannel : ArgonEntitlement.ManageMessages, ct))
            return DeleteMessageError.INSUFFICIENT_PERMISSIONS;

        // Soft delete: reports and audit trails reference messages by id, so the row has to outlive
        // its visibility. ArgonMessageEntity is not an ArgonEntity, so neither the global soft-delete
        // filter nor the timestamp interceptor covers it — both columns are set by hand here and the
        // read path filters on IsDeleted explicitly (see PgSqlMessagesLayout.QueryMessages).
        var affected = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .ExecuteUpdateAsync(s => s
               .SetProperty(m => m.IsDeleted, true)
               .SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow)
               .SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow), ct);

        // Lost the race with a concurrent delete — the message is gone either way, but say so
        // truthfully rather than broadcasting a second removal event for it.
        if (affected == 0)
            return DeleteMessageError.MESSAGE_NOT_FOUND;

        ForgetReactions(messageId);
        await DropPinAsync(ctx, messageId, callerId, ct);

        await FireChannel(new MessageDeleted(SpaceId, channelId, messageId, callerId), ct);

        return DeleteMessageError.NONE;
    }

    public async Task<bool> DeleteMessageByModeration(long messageId, Guid operatorId, CancellationToken ct = default)
    {
        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        // The same soft delete as above, minus the permission check: the report grain calling this
        // has already established that an operator decided, and the operator is not a member of
        // the space and holds no entitlement in it — which is exactly why the ordinary path cannot
        // be reused.
        var affected = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .ExecuteUpdateAsync(s => s
               .SetProperty(m => m.IsDeleted, true)
               .SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow)
               .SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow), ct);

        if (affected == 0)
            return false;

        ForgetReactions(messageId);
        await DropPinAsync(ctx, messageId, UserEntity.SystemUser, ct);

        logger.LogWarning("Moderation removed message {MessageId} in channel {ChannelId} of space {SpaceId} (operator {OperatorId})",
            messageId, channelId, SpaceId, operatorId);

        // Attributed to the system user: the operator's id is not a user id the clients know, and
        // the event only needs to say "gone".
        await FireChannel(new MessageDeleted(SpaceId, channelId, messageId, UserEntity.SystemUser), ct);

        return true;
    }

    /// <summary>
    /// Refuses a send that arrives inside the channel's cooldown. Slow mode is a tool moderators
    /// point at a room, not at themselves, so anyone holding <c>ManageMessages</c> — the same
    /// entitlement that lets them clean up afterwards — passes straight through.
    /// </summary>
    /// <summary>
    /// Refuses more than the configured number of messages a second into this channel.
    /// </summary>
    /// <remarks>
    /// Slow mode is a moderation tool, per author and chosen by whoever runs the space. This is not
    /// that: it is a ceiling on the channel as a whole, set by whoever runs the node, and it exists
    /// because the channel can measurably take more than anything legitimate will ever ask of it.
    /// <para>
    /// A whole second at a time rather than a smoothed bucket, because the point is to stop a
    /// runaway, not to pace a crowd — and a crowd that briefly bunches inside one second is exactly
    /// what should not be punished.
    /// </para>
    /// </remarks>
    private bool TakeChannelCap()
    {
        var limit = messageOptions.Value.PerChannelPerSecond;

        if (limit <= 0)
            return true;

        var now = DateTimeOffset.UtcNow;

        if (now - activation.State.CapSecond >= TimeSpan.FromSeconds(1))
        {
            activation.State.CapSecond   = now;
            activation.State.CapAccepted = 0;
        }

        return ++activation.State.CapAccepted <= limit;
    }

    private async Task<bool> IsSlowModeHoldingAsync(Guid senderId, Guid channelId)
    {
        if (_self.SlowMode is not { } window || window <= TimeSpan.Zero)
            return false;

        if (await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.ManageMessages))
            return false;

        return activation.State.LastSentBySender.TryGetValue(senderId, out var lastSentAt) && DateTimeOffset.UtcNow - lastSentAt < window;
    }

    /// <summary>
    /// The id already given to this randomId, or null if it is new.
    /// </summary>
    /// <remarks>
    /// The cache round trip was measured at 0.93 ms of a turn that had about 1.5 ms of work left in
    /// it, which made it the largest single thing standing between a channel and a thousand messages
    /// a second. Nearly every call is a miss — a new message has a new randomId — so paying for it on
    /// every send bought almost nothing.
    /// </remarks>
    private async Task<long?> DeduplicateAsync(ArgonMessageEntity message, long randomId)
    {
        if (activation.State.SentByRandomId.TryGetValue(randomId, out var known))
            return known;

        if (DateTimeOffset.UtcNow >= activation.State.DedupTrustedUntil)
            return null;

        return await messagesLayout.CheckDuplicationAsync(message, randomId);
    }

    private void RememberSend(long randomId, long messageId)
    {
        // Bounded rather than expiring: a retry arrives within seconds, so the last few thousand
        // sends are far more history than the question needs.
        if (activation.State.SentByRandomId.Count > 4096)
            activation.State.SentByRandomId.Clear();

        activation.State.SentByRandomId[randomId] = messageId;
    }

    private void NoteSent(Guid senderId)
    {
        if (_self.SlowMode is not { } window || window <= TimeSpan.Zero)
            return;

        // Everyone who ever posted here would otherwise stay in the dictionary for the lifetime of
        // the activation; entries older than the window can no longer block anyone, so drop them.
        if (activation.State.LastSentBySender.Count > 512)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            foreach (var stale in activation.State.LastSentBySender.Where(x => x.Value < cutoff).Select(x => x.Key).ToList())
                activation.State.LastSentBySender.Remove(stale);
        }

        activation.State.LastSentBySender[senderId] = DateTimeOffset.UtcNow;
    }

    public async Task<Either<string, VoiceInviteError>> CreateVoiceInvite(TimeSpan expiration, int maxUses, CancellationToken ct = default)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (_self.ChannelType != ChannelType.Voice)
            return VoiceInviteError.CHANNEL_IS_NOT_VOICE;

        // You cannot hand out a key to a room you cannot walk into yourself — Connect also implies
        // ViewChannel through the entitlement analyzer, so one check covers the path.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.Connect, ct))
            return VoiceInviteError.INSUFFICIENT_PERMISSIONS;

        try
        {
            var (error, code) = await GrainFactory.GetGrain<IServerInvitesGrain>(SpaceId)
               .CreateInviteLinkAsync(callerId, expiration, maxUses, channelId);

            return error switch
            {
                SpaceManageError.NONE          => code.inviteCode,
                SpaceManageError.NO_PERMISSION => VoiceInviteError.INSUFFICIENT_PERMISSIONS,
                _                              => VoiceInviteError.INTERNAL_ERROR
            };
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to create voice invite for channel {ChannelId}", channelId);
            return VoiceInviteError.INTERNAL_ERROR;
        }
    }

    public async Task<List<ArgonMessageEntity>> QueryMessages(long? @from, int limit)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        // ViewChannel as well: nothing implies it from ReadHistory, and a channel hidden by overwrites
        // must not be readable through its history. A refusal reads as an empty channel.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory))
            return [];

        var messages = await messagesLayout.QueryMessages(_self.SpaceId, channelId, @from, limit);
        await ResolveAttachmentUrls(messages);
        return messages;
    }

    public async Task<MessageWindowEntity> QueryMessagesAround(long messageId, int older, int newer)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        // As QueryMessages: ViewChannel and ReadHistory, and a refusal reads as an empty channel.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory))
            return MessageWindowEntity.Empty;

        var window = await messagesLayout.QueryMessagesAround(_self.SpaceId, channelId, messageId, older, newer);
        await ResolveAttachmentUrls(window.Messages);
        return window;
    }

    public async Task<(SendMessageError error, long messageId)> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo,
        List<ControlRowV1>? controls = null)
    {
        if (_self.ChannelType is not (ChannelType.Text or ChannelType.Announcement))
            return (SendMessageError.NOT_TEXT_CHANNEL, 0);

        if (_self.ChannelType == ChannelType.Announcement && this.IsBotCaller())
            return (SendMessageError.BOTS_NOT_ALLOWED, 0);

        if (text?.Length > messageOptions.Value.MaxTextLength)
            return (SendMessageError.TEXT_TOO_LONG, 0);

        if (entities?.Count > messageOptions.Value.MaxEntities)
            return (SendMessageError.INVALID_DATA, 0);

        if (controls is { Count: > 0 })
        {
            try
            {
                ControlRowV1.ValidateRows(controls);
            }
            catch (ArgumentException)
            {
                return (SendMessageError.INVALID_DATA, 0);
            }
        }

        var sw = Stopwatch.StartNew();
        var senderId = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        // Before the cap, so a caller who may not post cannot use up the channel's allowance.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.SendMessages))
            return (SendMessageError.NO_PERMISSION, 0);

        if (!TakeChannelCap())
            return (SendMessageError.CHANNEL_CAP, 0);

        if (await IsSlowModeHoldingAsync(senderId, channelId))
            return (SendMessageError.SLOW_MODE, 0);

        if (entities is { Count: > 0 } && entities.Any(e => e is MessageEntityAttachment or MessageEntityGif))
        {
            if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.AttachFiles))
                return (SendMessageError.NO_ATTACH_PERMISSION, 0);

            var attachmentCount = entities.Count(e => e is MessageEntityAttachment or MessageEntityGif);
            if (attachmentCount > 10)
                return (SendMessageError.TOO_MANY_ATTACHMENTS, 0);
        }
        
        var sanitized = SanitizeEntities(entities ?? []);
        await CacheGifEntitiesAsync(sanitized, senderId);
        await StripUnpingableMentionsAsync(sanitized, senderId);
        var pendingPreview = await PrepareLinkPreviewAsync(sanitized, text ?? "", senderId, channelId);

        var message = new ArgonMessageEntity
        {
            SpaceId   = _self.SpaceId,
            ChannelId = channelId,
            CreatorId = senderId,
            Entities  = sanitized,
            Controls  = controls,
            Text      = text ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            Reply     = replyTo,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var dup = await DeduplicateAsync(message, randomId);

        if (dup is not null)
        {
            sw.Stop();
            logger.LogInformation("Duplicate message detected, returning existing MessageId={MessageId}", dup.Value);
            return (SendMessageError.NONE, dup.Value);
        }

        var msgId = await messagesLayout.ExecuteInsertMessage(message, randomId);

        RememberSend(randomId, msgId);

        // Only a message that actually landed starts the next cooldown — a retry that de-duplicated
        // above returned earlier and must not push the author's window forward.
        NoteSent(senderId);

        message.MessageId = msgId;

        var dto = message.ToDto();

        await ResolveAttachmentUrls(message);
        dto = message.ToDto();

        // MessageSent stays SPACE-scoped (for now): clients derive unread badges for channels they
        // are NOT currently viewing from this event. Channel-scoping it needs the space-size gate
        // (large spaces → channel-scoped + pull-based unread; small spaces → space-scoped + live
        // unread), unlike typing/reactions/edits which have no cross-channel consumer.
        FireDetached(new MessageSent(_self.SpaceId, dto));

        // The card the crawler had not cached in time follows the message rather than holding it.
        if (pendingPreview is not null)
            _ = ResolveLinkPreviewLaterAsync(msgId, pendingPreview);

        // Two copies of the channel high-water mark, kept for two different readers. The durable one
        // is only noted here and written by the flush timer, into ChannelLastMessages — a row that
        // exists to carry this and nothing else, so that a message send touches no channel metadata.
        // It lived on the channel row until it turned the busiest channel in a space into the most
        // expensive row in the cluster to maintain. The cache copy is written per send because that
        // is the one anybody reading between two flushes will see.
        if (lastMessage.Raise(msgId))
            ChannelGrainInstrument.LastMessageAbsorbed.Add(1);

        PublishLastMessageId(msgId);

        // Process mentions asynchronously (don't block message delivery)
        _ = ProcessMentionsAsync(sanitized, msgId, senderId, replyTo);
        
        sw.Stop();
        
        ChannelGrainInstrument.MessagesSent.Add(1,
            new KeyValuePair<string, object?>("channel_type", "text"));
        
        ChannelGrainInstrument.MessageSendDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("channel_type", "text"),
            new KeyValuePair<string, object?>("has_reply", replyTo.HasValue ? "true" : "false"));
        
        logger.LogInformation("MessageSent event fired for MessageId={MessageId}", msgId);

        // Track message sent for stats
        _ = TrackMessageSentAsync(senderId);

        return (SendMessageError.NONE, msgId);
    }

    /// <summary>
    /// Settles XP for all users based on time since last membership change.
    /// Called before any Join/Leave to ensure correct memberCount for XP calculation.
    /// Solo users (memberCount == 1) get no XP.
    /// </summary>
    private async Task SettleXpForAllUsersAsync()
    {
        var memberCount = state.State.Users.Count;
        
        // Solo = no XP
        if (memberCount <= 1)
        {
            state.State.LastMembershipChange = DateTimeOffset.UtcNow;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var duration = now - state.State.LastMembershipChange;
        var durationSeconds = (int)Math.Min(duration.TotalSeconds, int.MaxValue);

        if (durationSeconds > 0)
        {
            // Award XP to all current users for this period
            foreach (var userId in state.State.UserJoinTimes.Keys)
            {
                var statsGrain = GrainFactory.GetGrain<IUserStatsGrain>(userId);
                await statsGrain.RecordVoiceTimeAsync(durationSeconds, this.GetPrimaryKey(), SpaceId);
            }
        }

        state.State.LastMembershipChange = now;
    }

    private async Task TrackCallJoinedAsync(Guid userId)
    {
        try
        {
            var statsGrain = GrainFactory.GetGrain<IUserStatsGrain>(userId);
            await statsGrain.IncrementCallsAsync();
        }
        catch
        {
            // Fire and forget - stats tracking should not fail main operation
        }
    }

    private async Task TrackMessageSentAsync(Guid userId)
    {
        try
        {
            var statsGrain = GrainFactory.GetGrain<IUserStatsGrain>(userId);
            await statsGrain.IncrementMessagesAsync();
        }
        catch
        {
            // Fire and forget - stats tracking should not fail main operation
        }
    }

    /// <summary>The cache cell holding a channel's newest message id, fresher than the durable copy.</summary>
    /// <remarks>
    /// The channel id goes in as the default <c>Guid.ToString()</c> ("D") form. That is the shape
    /// every reader of this cell was written against, so changing it here silently turns every read
    /// into a miss rather than breaking anything loudly.
    /// </remarks>
    private static string LastMessageCacheKey(Guid channelId) => ChannelHighWaterCell.KeyFor(channelId);

    /// <summary>
    /// Puts <paramref name="messageId"/> in the cache cell without holding the send turn for it.
    /// </summary>
    /// <remarks>
    /// Detached rather than awaited for the reason on <see cref="lastMessagePublishTail"/>, which is
    /// also why the write goes on the end of a chain instead of straight into the pool. A failure is
    /// logged and dropped: the cell is a freshness hint over the durable copy, so the worst a lost
    /// publish costs is a reader falling back to a value up to one flush interval old — the same
    /// answer they get for a channel that has not been written to since the key was evicted.
    /// </remarks>
    private void PublishLastMessageId(long messageId)
    {
        var sentAt    = DateTimeOffset.UtcNow;
        var channelId = this.GetPrimaryKey();
        var previous  = lastMessagePublishTail;

        lastMessagePublishTail = Task.Run(async () =>
        {
            // Never faults — the body below swallows everything — so this needs no guard of its own.
            await previous;

            try
            {
                await using var scope = redisPool.Rent();
                await scope.GetDatabase().StringSetAsync([
                    new(LastMessageCacheKey(channelId), messageId),
                    new(ChannelHighWaterCell.AtKeyFor(channelId), sentAt.ToUnixTimeMilliseconds())
                ]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish LastMessageId for channel {ChannelId}", channelId);
            }
        });
    }

    /// <summary>
    /// Writes everything the mark has absorbed since the previous flush, as a single row update.
    /// </summary>
    /// <remarks>
    /// Called from the shared flush timer and from deactivation, never from the send path. Doing
    /// nothing when nothing is owed is the common case for a quiet channel — an activation lives far
    /// longer than the conversation in it.
    /// </remarks>
    private async Task FlushLastMessageIdAsync()
    {
        if (!lastMessage.TryBeginFlush(out var messageId))
            return;

        var written = await UpdateLastMessageIdAsync(messageId);

        // Only a write that landed retires the mark, so a failed flush is retried on the next tick
        // rather than lost. This matters more than it did before: a per-message write that failed was
        // rewritten by the next message a moment later, whereas a flush carries every message since
        // the last one and a channel can fall silent immediately after it.
        if (written)
            lastMessage.CommitFlush(messageId);

        ChannelGrainInstrument.LastMessageFlushes.Add(1,
            new KeyValuePair<string, object?>("result", written ? "written" : "failed"));
    }

    /// <summary>
    /// Puts the mark in the one durable place it lives: its own row, in its own table.
    /// </summary>
    /// <remarks>
    /// <para>This used to be an <c>ExecuteUpdateAsync</c> against <c>Channels.LastMessageId</c>, and
    /// moving it is the whole point of <see cref="ChannelLastMessageEntity"/>: the channel row is
    /// metadata every client reads on bootstrap and wants replicating to every region, and a counter
    /// on it made that impossible. Nothing else about the flush changed — the coalescing, the timer,
    /// the flush on deactivation and the per-send Redis cell are all exactly as they were. Only the
    /// destination is different.</para>
    ///
    /// <para><b>An upsert, because the row need not exist.</b> Nothing creates it with the channel;
    /// it appears the first time somebody speaks. Raw SQL because EF has no upsert, and this shape —
    /// <c>INSERT … ON CONFLICT … DO UPDATE</c> — is what <c>ReadStateService</c> already uses and runs
    /// unmodified on both PostgreSQL and CockroachDB.</para>
    ///
    /// <para><b>The guard on the update is what makes the mark monotonic across activations.</b>
    /// Within one activation it cannot go backwards — <see cref="ChannelHighWaterMark"/> only rises —
    /// but a migrating channel has two activations alive at once for a moment, and the old one flushes
    /// on the way out. Without <c>WHERE … &lt; EXCLUDED</c> the loser of that race writes an older id
    /// over a newer one and every member's unread badge for the channel is wrong until the next
    /// message. Removing the clause makes no test fail and no log line appear.</para>
    ///
    /// <para><b>Rows affected is deliberately not consulted.</b> The guard makes zero rows the normal
    /// answer for "somebody else already wrote something newer", which is a flush that is done rather
    /// than a flush that failed. Treating it as a failure would keep the mark pending forever and
    /// rewrite the same statement every three seconds for the life of the activation. Only an
    /// exception means the write did not land.</para>
    /// </remarks>
    private async Task<bool> UpdateLastMessageIdAsync(long messageId)
    {
        var channelId = this.GetPrimaryKey();

        try
        {
            var now = DateTimeOffset.UtcNow;

            await using var ctx = await context.CreateDbContextAsync();

            for (var attempt = 0; ; attempt++)
            {
                var moved = await ctx.ChannelLastMessages
                   .Where(m => m.ChannelId == channelId && m.LastMessageId < messageId)
                   .ExecuteUpdateAsync(u => u
                       .SetProperty(m => m.LastMessageId, messageId)
                       .SetProperty(m => m.UpdatedAt, now));

                if (moved > 0 || attempt > 0 || await ctx.ChannelLastMessages.AnyAsync(m => m.ChannelId == channelId))
                    break;

                ctx.ChannelLastMessages.Add(new ChannelLastMessageEntity
                {
                    ChannelId     = channelId,
                    SpaceId       = SpaceId,
                    LastMessageId = messageId,
                    UpdatedAt     = now
                });

                try
                {
                    await ctx.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateException e) when (e.IsUniqueViolation())
                {
                    // Another flush created the row first; move it forward instead.
                    ctx.ChangeTracker.Clear();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update LastMessageId for channel {ChannelId}", channelId);
            return false;
        }
    }

    // URLs are built straight from the fileId — the API resolves the S3 key + region at fetch time
    // (see CdnRedirectFeature), so nothing region-specific is ever stored. The desktop client ignores
    // these and builds the same {api}/files/{fileId} URL itself; we still fill them for bot/API
    // consumers. No DB round-trip needed here anymore.
    private Task ResolveAttachmentUrls(List<ArgonMessageEntity> messages)
    {
        foreach (var message in messages)
            FillEntityUrls(message);
        return Task.CompletedTask;
    }

    private Task ResolveAttachmentUrls(ArgonMessageEntity message)
    {
        FillEntityUrls(message);
        return Task.CompletedTask;
    }

    private void FillEntityUrls(ArgonMessageEntity message)
    {
        if (message.Entities is not { Count: > 0 }) return;
        for (var i = 0; i < message.Entities.Count; i++)
        {
            if (message.Entities[i] is MessageEntityAttachment { downloadUrl: null } att)
                message.Entities[i] = att with { downloadUrl = s3.GetFileDownloadUrl(att.fileId) };
            if (message.Entities[i] is MessageEntityGif { previewUrl: null, fileId: not null } gif)
                message.Entities[i] = gif with { previewUrl = s3.GetFileDownloadUrl(gif.fileId.Value) };
        }
    }

    private async Task CacheGifEntitiesAsync(List<IMessageEntity> entities, Guid senderId)
    {
        for (var i = 0; i < entities.Count; i++)
        {
            if (entities[i] is not MessageEntityGif gif) continue;

            if (!klipy.ValidateUserHmac(gif.gifId, senderId, gif.hmac))
            {
                logger.LogWarning("Invalid GIF HMAC for slug={Slug}, user={UserId}", gif.gifId, senderId);
                entities.RemoveAt(i--);
                continue;
            }

            var cached = await klipy.EnsureCachedAsync(gif.gifId);
            if (cached is null)
            {
                logger.LogWarning("Failed to cache GIF: slug={Slug}", gif.gifId);
                entities.RemoveAt(i--);
                continue;
            }

            entities[i] = gif with { fileId = cached.Value.FileId };

            _ = this.GrainFactory.GetGrain<ISavedGifsGrain>(senderId).SaveGifAsync(gif.gifId);
        }
    }

    /// <summary>
    /// Settles the link-preview stub the client attached, if any, before the message is stored: the
    /// URL is checked, the sender's right to embed is checked, and the crawler is given
    /// <see cref="CrawlerOptions.SendBudget"/> to answer from its cache.
    /// </summary>
    /// <returns>
    /// The stub still waiting on the crawler, to be finished after the send by
    /// <see cref="ResolveLinkPreviewLaterAsync"/>; null when there is nothing left to do.
    /// </returns>
    private async Task<MessageEntityLinkPreview?> PrepareLinkPreviewAsync(List<IMessageEntity> entities, string text, Guid senderId, Guid channelId)
    {
        var stub = LinkPreviewEntities.TakeStub(entities, text);
        if (stub is null)
            return null;

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.PostEmbeddedLinks))
        {
            // Not an error: the link stays, the card does not — as if the client had never asked.
            entities.Remove(stub);
            return null;
        }

        var outcome = await linkPreviews.ResolveAsync(stub.url, crawlerOptions.Value.SendBudget);

        switch (outcome.Status)
        {
            case LinkPreviewStatus.Ready:
                entities[entities.IndexOf(stub)] = LinkPreviewEntities.Fill(stub, outcome.Preview!);
                return null;
            case LinkPreviewStatus.Unavailable when outcome.Retryable:
                // The crawler is on the page: the message goes out now and the card follows.
                return stub;
            default:
                entities.Remove(stub);
                return null;
        }
    }

    /// <summary>
    /// The second half of <see cref="PrepareLinkPreviewAsync"/>, off the send: waits the full lookup
    /// time for the crawler, then rewrites the stored entities and tells the channel through
    /// <see cref="MessageUpdated"/> — with the card, or without the stub when the page gave nothing.
    /// </summary>
    private async Task ResolveLinkPreviewLaterAsync(long messageId, MessageEntityLinkPreview stub)
    {
        var channelId = this.GetPrimaryKey();
        try
        {
            var outcome = await linkPreviews.ResolveAsync(stub.url, crawlerOptions.Value.Timeout);

            await using var ctx = await context.CreateDbContextAsync();

            var message = await ctx.Messages
               .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
               .FirstOrDefaultAsync();

            // Deleted in the meantime, or the stub is gone: nothing to tell anyone.
            if (message is null)
                return;

            var updated = new List<IMessageEntity>(message.Entities ?? []);
            var at      = updated.FindIndex(e => e is MessageEntityLinkPreview p && p.url == stub.url);
            if (at < 0)
                return;

            if (outcome.Status == LinkPreviewStatus.Ready)
                updated[at] = LinkPreviewEntities.Fill(stub, outcome.Preview!);
            else
                updated.RemoveAt(at);

            message.Entities  = updated;
            message.UpdatedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();

            await ResolveAttachmentUrls(message);
            await FireChannel(new MessageUpdated(SpaceId, channelId, message.ToDto()));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "link preview for message {MessageId} in channel {ChannelId} was not finished", messageId, channelId);
        }
    }

    /// <summary>
    ///     Strip client-provided downloadUrl from attachments to prevent URL injection.
    ///     URLs are resolved server-side at read time based on user geo.
    /// </summary>
    private static List<IMessageEntity> SanitizeEntities(List<IMessageEntity> entities)
    {
        for (var i = 0; i < entities.Count; i++)
        {
            if (entities[i] is MessageEntityAttachment att && att.downloadUrl is not null)
                entities[i] = att with { downloadUrl = null };
            if (entities[i] is MessageEntityGif gif && gif.previewUrl is not null)
                entities[i] = gif with { previewUrl = null };
        }
        return entities;
    }

    /// <summary>
    /// Takes one of the hour's mass pings for an announcement, or says there is none left. Only
    /// messages that actually pinged are counted, and deleting one does not give it back.
    /// </summary>
    private async Task<bool> TakeMassMentionBudgetAsync(long messageId)
    {
        var perHour = messageOptions.Value.AnnouncementMassMentionsPerHour;
        if (perHour <= 0)
            return true;

        if (massPings is null)
        {
            var seeded = await SeedMassPingsAsync(messageId, perHour);
            massPings ??= seeded;
        }

        var since = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var expired in massPings.Where(p => p.Value < since).Select(p => p.Key).ToList())
            massPings.Remove(expired);

        // Seeded already: an earlier message whose mentions were still in flight.
        if (massPings.ContainsKey(messageId))
            return true;

        if (massPings.Count >= perHour)
            return false;

        massPings[messageId] = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// The mass pings a previous activation may have sent this hour. A stored message does not say
    /// whether its ping went out, so the latest ones that could have are taken as having done so.
    /// </summary>
    private async Task<Dictionary<long, DateTimeOffset>> SeedMassPingsAsync(long before, int perHour)
    {
        var channelId = this.GetPrimaryKey();
        var since     = DateTimeOffset.UtcNow.AddHours(-1);
        var massUsers = messageOptions.Value.AnnouncementMassUserMentions;

        await using var ctx = await context.CreateDbContextAsync();
        var recent = await ctx.Messages
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.CreatedAt >= since && m.MessageId < before)
           .Select(m => new { m.MessageId, m.CreatorId, m.CreatedAt, m.Entities })
           .ToListAsync();

        return recent
           .Where(m => m.Entities.Any(e => e is MessageEntityMentionEveryone or MessageEntityMentionRole)
                    || m.Entities.OfType<MessageEntityMention>().Select(u => u.userId).Where(u => u != m.CreatorId).Distinct().Count() > massUsers)
           .OrderByDescending(m => m.MessageId)
           .Take(perHour)
           .ToDictionary(m => m.MessageId, m => m.CreatedAt);
    }

    /// <summary>
    /// The mentioned roles that ping: roles of this space marked mentionable, or any of its roles for
    /// a sender who may mention everyone. Never the everyone role, which only @everyone reaches.
    /// </summary>
    private async Task<HashSet<Guid>> PingableRolesAsync(IReadOnlySet<Guid> mentioned, bool mayPingAll)
    {
        var roles = await archetypeAgent.GetAllAsync(SpaceId);

        return roles
           .Where(a => mentioned.Contains(a.id) && !a.isDefault && (mayPingAll || a.isMentionable))
           .Select(a => a.id)
           .ToHashSet();
    }

    /// <summary>
    /// Takes out the @everyone and role mentions the sender may not ping, so no client shows them as
    /// pings either.
    /// </summary>
    private async Task StripUnpingableMentionsAsync(List<IMessageEntity> entities, Guid senderId)
    {
        var everyone = entities.Any(e => e is MessageEntityMentionEveryone);
        var roles    = entities.OfType<MessageEntityMentionRole>().Select(r => r.archetypeId).ToHashSet();
        if (!everyone && roles.Count == 0)
            return;

        var mayPingAll = await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), senderId, ArgonEntitlement.MentionEveryone);
        var pingable   = roles.Count > 0 ? await PingableRolesAsync(roles, mayPingAll) : [];

        entities.RemoveAll(e => e is MessageEntityMentionEveryone && !mayPingAll
                             || e is MessageEntityMentionRole r && !pingable.Contains(r.archetypeId));
    }

    /// <summary>The users among <paramref name="userIds"/> who are members of the space now.</summary>
    private async Task<List<Guid>> CurrentMembersAsync(List<Guid> userIds)
    {
        await using var ctx = await context.CreateDbContextAsync();
        return await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && userIds.Contains(m.UserId))
           .Select(m => m.UserId)
           .Distinct()
           .ToListAsync();
    }

    /// <remarks>
    /// <paramref name="entities"/> went through <see cref="StripUnpingableMentionsAsync"/> on the send,
    /// so every @everyone and role left in it is one the sender may ping.
    /// </remarks>
    private async Task ProcessMentionsAsync(List<IMessageEntity> entities, long messageId, Guid senderId, long? replyTo)
    {
        try
        {
            var readStateService = ServiceProvider.GetService<IReadStateService>();
            if (readStateService is null) return;

            if (replyTo.HasValue)
            {
                await using var msgCtx = await context.CreateDbContextAsync();
                var originalAuthor = await msgCtx.Messages
                    .AsNoTracking()
                    .Where(m => m.SpaceId == _self.SpaceId && m.ChannelId == this.GetPrimaryKey() && m.MessageId == replyTo.Value
                             && m.Crosspost == null)
                    .Select(m => m.CreatorId)
                    .FirstOrDefaultAsync();

                if (originalAuthor != default && originalAuthor != senderId)
                {
                    await readStateService.IncrementMentionsAsync(originalAuthor, this.GetPrimaryKey(), _self.SpaceId, 1);
                }
            }

            if (entities.Count == 0) return;

            var options = messageOptions.Value;

            var mentionedUsers = entities.OfType<MessageEntityMention>()
               .Select(m => m.userId)
               .Where(u => u != senderId)
               .Distinct()
               .Take(options.MaxMentionedUsers)
               .ToList();

            if (mentionedUsers.Count > 0)
                mentionedUsers = await CurrentMembersAsync(mentionedUsers);

            var hasEveryoneMention = entities.Any(e => e is MessageEntityMentionEveryone);
            var roleMentions       = entities.OfType<MessageEntityMentionRole>().DistinctBy(r => r.archetypeId).ToList();
            var massUserMention    = mentionedUsers.Count > options.AnnouncementMassUserMentions;

            if ((hasEveryoneMention || roleMentions.Count > 0 || massUserMention)
                && _self.ChannelType == ChannelType.Announcement
                && !await TakeMassMentionBudgetAsync(messageId))
            {
                hasEveryoneMention = false;
                roleMentions.Clear();
                if (massUserMention)
                    mentionedUsers.Clear();
            }

            if (mentionedUsers.Count > 0)
                await readStateService.BatchIncrementMentionsAsync(_self.SpaceId, this.GetPrimaryKey(), mentionedUsers);

            if (hasEveryoneMention || roleMentions.Count > 0)
            {
                var muteService = ServiceProvider.GetService<IMuteSettingsService>();
                if (muteService is null) return;

                await using var ctx = await context.CreateDbContextAsync();

                if (hasEveryoneMention)
                {
                    // Bounded probe: most spaces are small, so keep today's exact path (precise
                    // per-user mention write + immediate cache invalidation), loading at most
                    // EveryoneInlineCap+1 member ids so the silo heap is never flooded. Only very
                    // large spaces fall back to a fully set-based SQL upsert that materializes no
                    // member list (at the cost of TTL-based, not immediate, read-state cache refresh).
                    const int EveryoneInlineCap = 5000;

                    var members = await ctx.UsersToServerRelations
                        .Where(m => m.SpaceId == _self.SpaceId && m.UserId != senderId)
                        .Select(m => m.UserId)
                        .Take(EveryoneInlineCap + 1)
                        .ToListAsync();

                    if (members.Count <= EveryoneInlineCap)
                    {
                        var mutedUsers = await muteService.FilterMutedUsersAsync(this.GetPrimaryKey(), _self.SpaceId, members);
                        var suppressUsers = await ctx.Set<MuteSettingsEntity>()
                            .Where(m => members.Contains(m.UserId) && m.SuppressEveryone && (m.TargetId == _self.SpaceId || m.TargetId == this.GetPrimaryKey()))
                            .Select(m => m.UserId)
                            .Distinct()
                            .ToListAsync();

                        var targetUsers = members
                            .Where(u => !mutedUsers.Contains(u) && !suppressUsers.Contains(u))
                            .ToList();

                        await readStateService.BatchIncrementMentionsAsync(_self.SpaceId, this.GetPrimaryKey(), targetUsers);
                    }
                    else
                    {
                        // Heap-free set-based upsert for very large spaces (enumeration + mute/suppress
                        // exclusion happen entirely in SQL).
                        await readStateService.BumpEveryoneMentionsAsync(_self.SpaceId, this.GetPrimaryKey(), senderId);
                    }

                    await Fire(new BatchMentionOccurred(_self.SpaceId, this.GetPrimaryKey(), MentionTargetType.Everyone));
                }

                foreach (var roleMention in roleMentions)
                {
                    var roleMembers = await ctx.MemberArchetypes
                        .Where(m => m.ArchetypeId == roleMention.archetypeId)
                        .Select(m => m.ServerMember.UserId)
                        .Where(u => u != senderId)
                        .ToListAsync();

                    var mutedUsers = await muteService.FilterMutedUsersAsync(this.GetPrimaryKey(), _self.SpaceId, roleMembers);
                    var targetUsers = roleMembers.Where(u => !mutedUsers.Contains(u)).ToList();

                    await readStateService.BatchIncrementMentionsAsync(_self.SpaceId, this.GetPrimaryKey(), targetUsers);

                    await Fire(new BatchMentionOccurred(_self.SpaceId, this.GetPrimaryKey(), MentionTargetType.Role));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process mentions for message {MessageId} in channel {ChannelId}", messageId, this.GetPrimaryKey());
        }
    }

    private async Task<ChannelEntity> Get()
    {
        await using var ctx = await context.CreateDbContextAsync();

        // Activation fails either way; this names the id instead of "Sequence contains no elements".
        return await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == this.GetPrimaryKey())
            ?? throw new KeyNotFoundException($"Channel {this.GetPrimaryKey()} does not exist");
    }

    public async ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadAttachment(CancellationToken ct = default)
    {
        try
        {
            var userId = this.GetUserId();
            await using var ctx = await context.CreateDbContextAsync(ct);

            if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), userId, ArgonEntitlement.AttachFiles, ct))
                return UploadFileError.NOT_AUTHORIZED;

            var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(userId);
            var response = await fileGrain.RequestUploadAsync(
                new FileUploadRequest(FilePurpose.ChannelAttachment, "", 0, SpaceId, this.GetPrimaryKey()), ct);
            return new UploadTicket(response.BlobId, response.Url, response.Fields, response.TtlSeconds);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to begin upload attachment for channel {ChannelId}", this.GetPrimaryKey());
            return UploadFileError.INTERNAL_ERROR;
        }
    }

    public async ValueTask<AttachmentInfo> CompleteUploadAttachment(Guid blobId, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(userId);
        var fileInfo = await fileGrain.FinalizeUploadAsync(blobId, ct);

        return new AttachmentInfo(fileInfo.FileId, fileInfo.FileName ?? "", fileInfo.FileSize, fileInfo.ContentType ?? "",
            fileInfo.DownloadUrl);
    }

    public async Task<IInvokeSlashCommandResult> InvokeSlashCommand(Guid commandId, List<SlashCommandOption> options)
    {
        var sw = Stopwatch.StartNew();
        BotApiInstrument.CommandInvocations.Add(1);

        var senderId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        // Check UseCommands permission
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.UseCommands))
        {
            BotApiInstrument.CommandErrors.Add(1,
                new KeyValuePair<string, object?>("error", "insufficient_permissions"));
            return new FailedInvokeSlashCommand(InvokeSlashCommandError.INSUFFICIENT_PERMISSIONS);
        }

        // Single query: command + bot + installation check via JOIN
        var commandInfo = await ctx.BotCommands
           .AsNoTracking()
           .Where(c => c.CommandId == commandId
                       && (c.SpaceId == SpaceId || c.SpaceId == null))
           .Join(ctx.BotEntities.AsNoTracking(),
                c => c.AppId,
                b => b.AppId,
                (c, b) => new { c.CommandId, c.Name, c.Options, c.AppId, b.BotAsUserId })
           .Join(ctx.UsersToServerRelations.AsNoTracking().Where(r => r.SpaceId == SpaceId),
                cb => cb.BotAsUserId,
                r => r.UserId,
                (cb, _) => new { cb.CommandId, cb.Name, cb.Options, cb.AppId, cb.BotAsUserId })
           .FirstOrDefaultAsync();

        if (commandInfo is null)
        {
            BotApiInstrument.CommandErrors.Add(1,
                new KeyValuePair<string, object?>("error", "command_not_found"));
            return new FailedInvokeSlashCommand(InvokeSlashCommandError.COMMAND_NOT_FOUND);
        }

        // Resolve invoking user
        var user = await botUserCache.GetOrResolveAsync(senderId);

        // Map options: build lookup for O(1) access
        var schemaLookup = commandInfo.Options.ToDictionary(o => o.Name);
        var mappedOptions = new List<BotCommandOptionValueV1>(options.Count);
        foreach (var opt in options)
        {
            if (!schemaLookup.TryGetValue(opt.name, out var schema)) continue;

            object typedValue = schema.Type switch
            {
                Core.Entities.Data.BotCommandOptionType.Integer => long.TryParse(opt.value, out var l) ? l : opt.value,
                Core.Entities.Data.BotCommandOptionType.Number  => double.TryParse(opt.value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : opt.value,
                Core.Entities.Data.BotCommandOptionType.Boolean => bool.TryParse(opt.value, out var b) ? b : opt.value,
                _                            => opt.value
            };

            mappedOptions.Add(new BotCommandOptionValueV1(opt.name, (Features.BotApi.BotCommandOptionType)(int)schema.Type, typedValue));
        }

        // Generate correlation ID and publish CommandInteractionEvent to the bot
        var interactionId = ArgonId.New();

        await botEventPublisher.PublishCommandInteractionAsync(
            interactionId, SpaceId, channelId, commandInfo.CommandId, commandInfo.Name, user, mappedOptions,
            senderId, commandInfo.AppId);

        sw.Stop();
        BotApiInstrument.CommandDispatchDuration.Record(sw.Elapsed.TotalMilliseconds);

        return new SuccessInvokeSlashCommand();
    }

    public async Task<IInteractWithControlResult> InteractWithControl(long messageId, string controlId)
    {
        var senderId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        // Load the message
        var message = await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .Select(m => new { m.MessageId, m.CreatorId, m.Controls })
           .FirstOrDefaultAsync();

        if (message is null)
            return new FailedInteractWithControl(InteractWithControlError.MESSAGE_NOT_FOUND);

        // Find the control by Id
        if (message.Controls is null or { Count: 0 })
            return new FailedInteractWithControl(InteractWithControlError.CONTROL_NOT_FOUND);

        BotControlV1? control = null;
        foreach (var row in message.Controls)
        {
            control = row.Controls.FirstOrDefault(c => c.Id == controlId);
            if (control is not null) break;
        }

        if (control is null)
            return new FailedInteractWithControl(InteractWithControlError.CONTROL_NOT_FOUND);

        if (control.Disabled == true)
            return new FailedInteractWithControl(InteractWithControlError.CONTROL_DISABLED);

        // Check archetype constraint (exact match + admin bypass)
        if (control.RequiredArchetypeId is { } requiredId)
        {
            var hasArchetype = await ctx.MemberArchetypes
               .AsNoTracking()
               .AnyAsync(ma => ma.Archetype.SpaceId == SpaceId
                            && ma.ServerMember.UserId == senderId
                            && ma.ArchetypeId == requiredId);
            if (!hasArchetype
                && !await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), senderId, ArgonEntitlement.ManageServer))
                return new FailedInteractWithControl(InteractWithControlError.ARCHETYPE_REQUIRED);
        }

        // Verify the message author is a bot installed in this space
        var botInfo = await ctx.BotEntities
           .AsNoTracking()
           .Where(b => b.BotAsUserId == message.CreatorId)
           .Join(ctx.UsersToServerRelations.AsNoTracking().Where(r => r.SpaceId == SpaceId),
                b => b.BotAsUserId, r => r.UserId,
                (b, _) => new { b.BotAsUserId, b.AppId })
           .FirstOrDefaultAsync();

        if (botInfo is null)
            return new FailedInteractWithControl(InteractWithControlError.BOT_NOT_CONNECTED);

        // Generate correlation ID and publish
        var interactionId = ArgonId.New();
        var user = await botUserCache.GetOrResolveAsync(senderId);

        await botEventPublisher.PublishControlInteractionAsync(
            interactionId, control.Type, messageId, channelId, SpaceId, user, controlId,
            senderId, botInfo.AppId);

        return new SuccessInteractWithControl(interactionId);
    }

    public async Task<IInteractWithSelectResult> InteractWithSelect(long messageId, string customId, List<string> values)
    {
        var senderId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        var message = await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .Select(m => new { m.MessageId, m.CreatorId, m.Controls })
           .FirstOrDefaultAsync();

        if (message is null)
            return new FailedInteractWithSelect(InteractWithSelectError.MESSAGE_NOT_FOUND);

        if (message.Controls is null or { Count: 0 })
            return new FailedInteractWithSelect(InteractWithSelectError.CONTROL_NOT_FOUND);

        BotControlV1? control = null;
        foreach (var row in message.Controls)
        {
            control = row.Controls.FirstOrDefault(c => c.CustomId == customId);
            if (control is not null) break;
        }

        if (control is null)
            return new FailedInteractWithSelect(InteractWithSelectError.CONTROL_NOT_FOUND);

        if (control.Type == ControlType.Button)
            return new FailedInteractWithSelect(InteractWithSelectError.NOT_A_SELECT);

        if (control.Disabled == true)
            return new FailedInteractWithSelect(InteractWithSelectError.CONTROL_DISABLED);

        // Check archetype constraint (exact match + admin bypass)
        if (control.RequiredArchetypeId is { } requiredId)
        {
            var hasArchetype = await ctx.MemberArchetypes
               .AsNoTracking()
               .AnyAsync(ma => ma.Archetype.SpaceId == SpaceId
                            && ma.ServerMember.UserId == senderId
                            && ma.ArchetypeId == requiredId);
            if (!hasArchetype
                && !await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), senderId, ArgonEntitlement.ManageServer))
                return new FailedInteractWithSelect(InteractWithSelectError.ARCHETYPE_REQUIRED);
        }

        var minValues = control.MinValues ?? 1;
        var maxValues = control.MaxValues ?? 1;
        if (values.Count < minValues || values.Count > maxValues)
            return new FailedInteractWithSelect(InteractWithSelectError.INVALID_VALUES);

        // For StringSelect, validate values are in the allowed options
        if (control.Type == ControlType.StringSelect && control.Options is { Count: > 0 })
        {
            var allowed = control.Options.Select(o => o.Value).ToHashSet();
            if (values.Any(v => !allowed.Contains(v)))
                return new FailedInteractWithSelect(InteractWithSelectError.INVALID_VALUES);
        }

        var botInfo = await ctx.BotEntities
           .AsNoTracking()
           .Where(b => b.BotAsUserId == message.CreatorId)
           .Join(ctx.UsersToServerRelations.AsNoTracking().Where(r => r.SpaceId == SpaceId),
                b => b.BotAsUserId, r => r.UserId,
                (b, _) => new { b.BotAsUserId, b.AppId })
           .FirstOrDefaultAsync();

        if (botInfo is null)
            return new FailedInteractWithSelect(InteractWithSelectError.BOT_NOT_CONNECTED);

        var interactionId = ArgonId.New();
        var user = await botUserCache.GetOrResolveAsync(senderId);

        await botEventPublisher.PublishSelectInteractionAsync(
            interactionId, control.Type, customId, messageId, channelId, SpaceId, user, values,
            senderId, botInfo.AppId);

        return new SuccessInteractWithSelect(interactionId);
    }

    public async Task<ISubmitModalResult> SubmitModal(Guid interactionId, List<ModalSubmitValue> values)
    {
        var senderId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        var ctx = botEventPublisher.InteractionStore.TryPeek(interactionId);
        if (ctx is null)
            return new FailedSubmitModal(SubmitModalError.INTERACTION_EXPIRED);

        // Checked before consuming, so a submit from anyone else cannot use up the member's modal.
        if (ctx.UserId != senderId || ctx.ChannelId != channelId)
            return new FailedSubmitModal(SubmitModalError.INTERACTION_NOT_FOUND);

        if (botEventPublisher.InteractionStore.TryConsume(interactionId) is null)
            return new FailedSubmitModal(SubmitModalError.INTERACTION_EXPIRED);

        var user = await botUserCache.GetOrResolveAsync(senderId);

        var customId = interactionId.ToString();
        var mappedValues = values
           .Select(v => new ModalSubmitValueV1(v.customId, [v.value]))
           .ToList();

        await botEventPublisher.PublishModalSubmitAsync(
            ArgonId.New(), customId, channelId, SpaceId, user, mappedValues);

        return new SuccessSubmitModal();
    }

    public async Task EditBotMessage(long messageId, Guid botUserId, string? text, List<ControlRowV1>? controls)
    {
        // What a bot may not post there, it may not rewrite there either.
        if (_self.ChannelType == ChannelType.Announcement)
            throw new UnauthorizedAccessException("Bots cannot post in announcement channels");

        if (text?.Length > messageOptions.Value.MaxTextLength)
            throw new InvalidOperationException($"Message text is longer than {messageOptions.Value.MaxTextLength} characters");

        if (controls is { Count: > 0 })
            ControlRowV1.ValidateRows(controls);

        var channelId = this.GetPrimaryKey();
        await using var ctx = await context.CreateDbContextAsync();

        var message = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && m.CreatorId == botUserId && !m.IsDeleted)
           .FirstOrDefaultAsync();

        if (message is null)
            throw new InvalidOperationException("Message not found or not owned by this bot.");

        if (text is not null)
            message.Text = text;

        if (controls is not null)
            message.Controls = controls.Count == 0 ? null : controls;

        message.UpdatedAt = DateTimeOffset.UtcNow;
        if (text is not null)
            message.EditedAt = message.UpdatedAt;
        await ctx.SaveChangesAsync();

        await FireChannel(new MessageEdited(SpaceId, channelId, messageId, message.Text, message.UpdatedAt.UtcDateTime));

        await ResolveAttachmentUrls(message);
        await FireChannel(new MessageUpdated(SpaceId, channelId, message.ToDto()));
    }

    public async Task<IEditMessageResult> EditMessage(long messageId, string text, List<IMessageEntity> entities)
    {
        var userId    = this.GetUserId();
        var channelId = this.GetPrimaryKey();
        text ??= "";

        if (text.Length > messageOptions.Value.MaxTextLength || entities?.Count > messageOptions.Value.MaxEntities)
            return new FailedEditMessage(EditMessageError.MESSAGE_TOO_LONG);

        // An author who was banned, left, or lost the right to post does not get to rewrite the record.
        // SendMessages also covers membership and ViewChannel.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, userId, ArgonEntitlement.SendMessages))
            return new FailedEditMessage(EditMessageError.INSUFFICIENT_PERMISSIONS);

        await using var ctx = await context.CreateDbContextAsync();

        var message = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .FirstOrDefaultAsync();

        if (message is null)
            return new FailedEditMessage(EditMessageError.MESSAGE_NOT_FOUND);

        // A crossposted copy is not its author's to rewrite where it landed.
        if (message.CreatorId != userId || message.Crosspost is not null)
            return new FailedEditMessage(EditMessageError.NOT_AUTHOR);

        // Attachments and link cards are not the client's to rewrite: keep the stored ones, and a card
        // only while its link is still in the text.
        var kept = (message.Entities ?? [])
           .Where(e => e is MessageEntityAttachment or MessageEntityGif
                    || e is MessageEntityLinkPreview p && text.Contains(p.url, StringComparison.Ordinal))
           .ToList();

        if (string.IsNullOrWhiteSpace(text) && !kept.Any(e => e is MessageEntityAttachment or MessageEntityGif))
            return new FailedEditMessage(EditMessageError.EMPTY_MESSAGE);

        var edited = (entities ?? [])
           .Where(e => e is not (MessageEntityAttachment or MessageEntityGif or MessageEntityLinkPreview
                               or MessageEntitySystemCallStarted or MessageEntitySystemCallEnded
                               or MessageEntitySystemCallTimeout or MessageEntitySystemUserJoined))
           .Concat(kept)
           .ToList();

        // An edit pings nobody either way; this keeps it from adding a highlight the author may not use.
        await StripUnpingableMentionsAsync(edited, userId);

        message.Text      = text;
        message.Entities  = edited;
        message.UpdatedAt = DateTimeOffset.UtcNow;
        message.EditedAt  = message.UpdatedAt;
        await ctx.SaveChangesAsync();

        await ResolveAttachmentUrls(message);
        var dto = message.ToDto();
        await FireChannel(new MessageUpdated(SpaceId, channelId, dto));

        return new SuccessEditMessage(dto);
    }

    // ── Reactions (buffered writes) ──────────────────────────

    public async Task<IAddReactionResult> AddReaction(long messageId, string emoji)
    {
        if (_self.ChannelType is not (ChannelType.Text or ChannelType.Announcement))
        {
            ChannelGrainInstrument.ReactionsAdded.Add(1,
                new KeyValuePair<string, object?>("result", "invalid_channel"));
            return new FailedAddReaction(AddReactionError.NONE);
        }

        if (RefuseReactionIfDisabled() is { } refused)
            return refused;

        var userId = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, userId, ArgonEntitlement.AddReactions))
        {
            ChannelGrainInstrument.ReactionsAdded.Add(1,
                new KeyValuePair<string, object?>("result", "no_permission"));
            return new FailedAddReaction(AddReactionError.INSUFFICIENT_PERMISSIONS);
        }

        var reactions = await LoadReactionsAsync(messageId);
        if (reactions is null)
        {
            ChannelGrainInstrument.ReactionsAdded.Add(1,
                new KeyValuePair<string, object?>("result", "message_not_found"));
            return new FailedAddReaction(AddReactionError.MESSAGE_NOT_FOUND);
        }

        var existing = reactions.FirstOrDefault(r => r.Emoji == emoji);
        if (existing is not null)
        {
            if (existing.UserIds.Contains(userId))
            {
                ChannelGrainInstrument.ReactionsAdded.Add(1,
                    new KeyValuePair<string, object?>("result", "already_reacted"));
                return new FailedAddReaction(AddReactionError.ALREADY_REACTED);
            }

            existing.UserIds.Add(userId);
        }
        else
        {
            if (reactions.Count >= 20)
            {
                ChannelGrainInstrument.ReactionsAdded.Add(1,
                    new KeyValuePair<string, object?>("result", "limit_reached"));
                return new FailedAddReaction(AddReactionError.REACTION_LIMIT_REACHED);
            }

            reactions.Add(new MessageReactionData { Emoji = emoji, UserIds = [userId] });
        }

        _dirtyReactions.Add(messageId);

        ChannelGrainInstrument.ReactionsAdded.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        await FireChannel(new ReactionAdded(SpaceId, channelId, messageId, userId, emoji, null));

        return new SuccessAddReaction();
    }

    public async Task<IRemoveReactionResult> RemoveReaction(long messageId, string emoji)
    {
        var userId = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        var reactions = await LoadReactionsAsync(messageId);
        if (reactions is null)
        {
            ChannelGrainInstrument.ReactionsRemoved.Add(1,
                new KeyValuePair<string, object?>("result", "message_not_found"));
            return new FailedRemoveReaction(RemoveReactionError.MESSAGE_NOT_FOUND);
        }

        var existing = reactions.FirstOrDefault(r => r.Emoji == emoji);
        if (existing is null || !existing.UserIds.Remove(userId))
        {
            ChannelGrainInstrument.ReactionsRemoved.Add(1,
                new KeyValuePair<string, object?>("result", "not_found"));
            return new FailedRemoveReaction(RemoveReactionError.REACTION_NOT_FOUND);
        }

        if (existing.UserIds.Count == 0)
            reactions.Remove(existing);

        _dirtyReactions.Add(messageId);

        ChannelGrainInstrument.ReactionsRemoved.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        await FireChannel(new ReactionRemoved(SpaceId, channelId, messageId, userId, emoji));

        return new SuccessRemoveReaction();
    }

    public async Task<Dictionary<long, List<ReactionInfo>>> BatchGetReactions(List<long> messageIds)
    {
        var callerId = this.GetUserId();

        // The refusal QueryMessages gives: who reacted with what is part of the history.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), callerId,
                ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory))
            return new();

        const int maxBatch = 50;
        var ids = messageIds.Count > maxBatch ? messageIds.Take(maxBatch).ToList() : messageIds;

        var result = new Dictionary<long, List<ReactionInfo>>(ids.Count);

        // Partition into cached and uncached
        var uncachedIds = new List<long>();
        foreach (var id in ids)
        {
            if (_reactionCache.TryGetValue(id, out var cached))
            {
                _reactionLru.Remove(id);
                _reactionLru.AddFirst(id);
                result[id] = ToReactionInfoList(cached);
            }
            else
            {
                uncachedIds.Add(id);
            }
        }

        // Batch-load uncached from DB in one query
        if (uncachedIds.Count > 0)
        {
            await using var ctx = await context.CreateDbContextAsync();
            var channelId = this.GetPrimaryKey();

            var rows = await ctx.Messages
               .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && uncachedIds.Contains(m.MessageId) && !m.IsDeleted)
               .Select(m => new { m.MessageId, m.Reactions })
               .ToListAsync();

            foreach (var row in rows)
            {
                var reactions = row.Reactions ?? [];
                _reactionCache[row.MessageId] = reactions;
                _reactionLru.AddFirst(row.MessageId);
                result[row.MessageId] = ToReactionInfoList(reactions);
            }

            // Evict non-dirty entries if cache grew too large
            while (_reactionLru.Count > MaxCachedReactionMessages)
            {
                var oldest = _reactionLru.Last!.Value;
                if (_dirtyReactions.Contains(oldest))
                    break;
                _reactionLru.RemoveLast();
                _reactionCache.Remove(oldest);
            }
        }

        return result;

        static List<ReactionInfo> ToReactionInfoList(List<MessageReactionData> data)
            => data.Select(r => new ReactionInfo(
                r.Emoji, r.CustomEmojiId, r.UserIds.Count,
                r.UserIds.Take(ArgonMessageEntity.ReactionUserPreviewLimit).ToList())).ToList();
    }

    private async Task<List<MessageReactionData>?> LoadReactionsAsync(long messageId)
    {
        if (_reactionCache.TryGetValue(messageId, out var cached))
        {
            // Move to front of LRU
            _reactionLru.Remove(messageId);
            _reactionLru.AddFirst(messageId);
            return cached;
        }

        await using var ctx = await context.CreateDbContextAsync();
        var message = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == this.GetPrimaryKey() && m.MessageId == messageId && !m.IsDeleted)
           .Select(m => new { m.Reactions })
           .FirstOrDefaultAsync();

        if (message is null)
            return null;

        var reactions = message.Reactions ?? [];
        _reactionCache[messageId] = reactions;
        _reactionLru.AddFirst(messageId);

        // Evict non-dirty entries if cache is too large
        while (_reactionLru.Count > MaxCachedReactionMessages)
        {
            var oldest = _reactionLru.Last!.Value;
            if (_dirtyReactions.Contains(oldest))
                break; // Don't evict dirty entries
            _reactionLru.RemoveLast();
            _reactionCache.Remove(oldest);
        }

        return reactions;
    }

    // A pending write for the message stays in _dirtyReactions; the flush skips ids no longer cached.
    private void ForgetReactions(long messageId)
    {
        if (_reactionCache.Remove(messageId))
            _reactionLru.Remove(messageId);
    }

    private async Task FlushReactionsAsync()
    {
        if (_dirtyReactions.Count == 0)
            return;

        var toFlush = _dirtyReactions.ToList();
        _dirtyReactions.Clear();

        var messageIds = toFlush.Where(_reactionCache.ContainsKey).ToList();

        if (messageIds.Count == 0)
            return;

        await using var ctx = await context.CreateDbContextAsync();
        var channelId = this.GetPrimaryKey();

        var messages = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && messageIds.Contains(m.MessageId))
           .ToListAsync();

        foreach (var message in messages)
        {
            var reactions = _reactionCache[message.MessageId];
            message.Reactions = reactions.Count == 0 ? null : reactions.ToList();

            // A pinned copy outlives the reaction cache entry, which may be evicted once this is written.
            if (pinnedMessages.TryGetValue(message.MessageId, out var pinned))
                pinned.Reactions = message.Reactions;
        }

        // One batched round trip for every dirty message.
        await ctx.SaveChangesAsync();
    }
}

/// <summary>
/// What a channel activation holds that its persisted state does not.
/// </summary>
/// <remarks>
/// <para>Distinct from <see cref="ChannelGrainState"/>, which is written to storage and outlives the
/// activation. This is the opposite: memory that is meaningless once the activation ends, and worth
/// exactly one thing — surviving a move to another silo without being rebuilt from nothing.</para>
///
/// <para>Held as <c>IPersistentState</c> against the storage that stores nothing, which is what makes
/// it travel: Orleans' state bridge is itself an <c>IGrainMigrationParticipant</c>, so declaring the
/// state is the whole of the work and nothing is packed or unpacked by hand. Adding a field later is
/// one line here and no lines anywhere else.</para>
/// </remarks>
[GenerateSerializer]
public sealed record ChannelActivationState
{
    /// <summary>Until when this activation's own dedup memory is the whole answer.</summary>
    [Id(0)]
    public DateTimeOffset DedupTrustedUntil { get; set; }

    /// <summary>Client-supplied random id to the message id it produced.</summary>
    [Id(1)]
    public Dictionary<long, long> SentByRandomId { get; set; } = new();

    /// <summary>Last accepted send per sender, for slow mode.</summary>
    [Id(2)]
    public Dictionary<Guid, DateTimeOffset> LastSentBySender { get; set; } = new();

    /// <summary>The one-second window the channel-wide cap is currently counting in.</summary>
    [Id(3)]
    public DateTimeOffset CapSecond { get; set; }

    [Id(4)]
    public int CapAccepted { get; set; }

    /// <summary>The screencast drawing session, if one is open.</summary>
    [Id(5)]
    public DrawingSessionState? DrawingSession { get; set; }

    /// <summary>
    /// Bots that are mid-typing. Kept beside the timers rather than derived from them: a timer cannot
    /// travel, so the new activation arms fresh ones from this — the indicator can outlive a move by
    /// up to one timeout and never longer.
    /// </summary>
    [Id(6)]
    public HashSet<Guid> BotTyping { get; set; } = [];

    /// <summary>
    /// Set once an activation has run. Absent on a fresh one, present on a migrated one — which is
    /// how the grain tells them apart, and the difference decides whether the voice roster is reset.
    /// </summary>
    [Id(7)]
    public bool Activated { get; set; }
}

/// <param name="SessionId">Identifies the session to clients across the move.</param>
/// <param name="StreamerId">Who is sharing.</param>
/// <param name="AllowedDrawers">Who may draw on the share.</param>
[GenerateSerializer]
public sealed record DrawingSessionState(
    [property: Id(0)] string SessionId,
    [property: Id(1)] Guid StreamerId,
    [property: Id(2)] HashSet<Guid> AllowedDrawers);
