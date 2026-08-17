namespace Argon.Grains;

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
using Argon.Features.Integrations.Klipy;
using Argon.Core.Features.CoreLogic.Privacy;
using Core.Entities.Data;
using ion.runtime;

public class ChannelGrain(
    [PersistentState("channel-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<ChannelGrainState> state,
    IDbContextFactory<ApplicationDbContext> context,
    IMessagesLayout messagesLayout,
    IEntitlementChecker entitlementChecker,
    AppHubServer appHubServer,
    BotEventPublisher botEventPublisher,
    BotUserCache botUserCache,
    IS3StorageService s3,
    IKlipyService klipy,
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

    // ── Screencast drawing session (ephemeral, lives with the share) ──
    private const int DrawingDefaultTtlMs = 6000;
    private (string SessionId, Guid StreamerId, HashSet<Guid> AllowedDrawers)? _drawingSession;

    private readonly Dictionary<Guid, DateTimeOffset> _lastSentBySender = new();

    private Task Fire<T>(T ev, CancellationToken ct = default) where T : IArgonEvent
        => appHubServer.BroadcastSpace(ev, SpaceId, ct);

    // Channel-scoped delivery for high-frequency channel content (messages, edits, reactions,
    // typing): reaches only clients currently viewing THIS channel, not all members of the space.
    // Space-wide events (voice membership, recording, meetings, mentions) keep using Fire().
    private Task FireChannel<T>(T ev, CancellationToken ct = default) where T : IArgonEvent
        => appHubServer.BroadcastChannel(ev, SpaceId, this.GetPrimaryKey(), ct);

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _self = await Get();

        await state.ReadStateAsync(cancellationToken);

        state.State.Users.Clear();
        state.State.UserJoinTimes.Clear();
        state.State.LastMembershipChange = DateTimeOffset.UtcNow;
        state.State.EgressActive = false;

        await state.WriteStateAsync(cancellationToken);

        _reactionFlushTimer = this.RegisterGrainTimer(
            async _ => await FlushReactionsAsync(),
            new GrainTimerCreationOptions(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Flush pending reactions before shutdown
        await FlushReactionsAsync();

        // Settle XP for all users still in channel
        await SettleXpForAllUsersAsync();

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

        ChannelGrainInstrument.TypingEvents.Add(1,
            new KeyValuePair<string, object?>("event_type", "bot_typing"));

        // Cancel existing auto-stop timer for this user if any
        if (_botTypingTimers.Remove(userId, out var existing))
            existing.Dispose();

        await FireChannel(new UserTypingEvent(SpaceId, channelId, userId, kind));

        // Register auto-stop timer — fires UserStopTypingEvent after timeout
        _botTypingTimers[userId] = this.RegisterGrainTimer(async _ =>
        {
            _botTypingTimers.Remove(userId);
            await FireChannel(new UserStopTypingEvent(SpaceId, channelId, userId));
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

        var result = await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty)
           .KickParticipantAsync(new ArgonUserId(memberId), new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()));

        ChannelGrainInstrument.MemberKicks.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        return result;
    }

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

        if (state.State.UserJoinTimes.TryGetValue(userId, out _))
        {
            await SettleXpForAllUsersAsync();
            state.State.UserJoinTimes.Remove(userId);
            state.State.Users.Remove(userId);
            await Fire(new LeavedFromChannelUser(SpaceId, this.GetPrimaryKey(), userId));
            await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserLeftVoiceAsync(userId);
        }

        // Settle XP for existing users before adding new one
        await SettleXpForAllUsersAsync();

        state.State.Users.Add(userId, new RealtimeChannelUser(userId, ChannelMemberState.NONE));
        state.State.UserJoinTimes[userId] = DateTimeOffset.UtcNow;
        await state.WriteStateAsync();

        // Track call joined for stats
        _ = TrackCallJoinedAsync(userId);

        await Fire(new JoinedToChannelUser(SpaceId, this.GetPrimaryKey(), userId));
        await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserJoinedVoiceAsync(userId, this.GetPrimaryKey(), DateTimeOffset.UtcNow);

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        ChannelGrainInstrument.VoiceJoins.Add(1,
            new KeyValuePair<string, object?>("source", "direct"));
        
        ChannelGrainInstrument.VoiceActiveUsers.Record(state.State.Users.Count);

        return await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty).IssueAuthorizationTokenAsync(new ArgonUserId(userId),
            new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()), SfuPermissionKind.DefaultUser);
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

        var sessionId = Guid.NewGuid().ToString("N");
        _drawingSession = (sessionId, streamerId, allowed.ToHashSet());

        await Fire(new DrawingSessionStarted(
            SpaceId, this.GetPrimaryKey(), sessionId, streamerId,
            new IonArray<Guid>(allowed), DrawingDefaultTtlMs));

        return new DrawingSessionDescriptor(sessionId, streamerId, allowed, DrawingDefaultTtlMs);
    }

    public async Task<bool> StopDrawingSession(string sessionId)
    {
        if (_drawingSession is not { } session) return false;
        if (session.SessionId != sessionId) return false;
        if (session.StreamerId != this.GetUserId()) return false; // only the streamer may close

        _drawingSession = null;
        await Fire(new DrawingSessionEnded(SpaceId, this.GetPrimaryKey(), sessionId));
        return true;
    }

    public async Task Leave(Guid userId)
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
        await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserLeftVoiceAsync(userId);
        await state.WriteStateAsync();

        // End the streamer's drawing session if they left the channel.
        if (_drawingSession is { } ds && ds.StreamerId == userId)
        {
            var sessionId = ds.SessionId;
            _drawingSession = null;
            await Fire(new DrawingSessionEnded(SpaceId, this.GetPrimaryKey(), sessionId));
        }

        if (state.State.Users.Count == 0)
            this.DelayDeactivation(TimeSpan.MinValue);

        ChannelGrainInstrument.VoiceLeaves.Add(1,
            new KeyValuePair<string, object?>("source", "direct"));
        
        ChannelGrainInstrument.VoiceActiveUsers.Record(state.State.Users.Count);
    }

    public async Task OnParticipantJoined(Guid userId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return;

        if (state.State.Users.ContainsKey(userId))
            return;

        await SettleXpForAllUsersAsync();

        state.State.Users.Add(userId, new RealtimeChannelUser(userId, ChannelMemberState.NONE));
        state.State.UserJoinTimes[userId] = DateTimeOffset.UtcNow;
        await state.WriteStateAsync();

        await Fire(new JoinedToChannelUser(SpaceId, this.GetPrimaryKey(), userId));
        await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserJoinedVoiceAsync(userId, this.GetPrimaryKey(), DateTimeOffset.UtcNow);

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        ChannelGrainInstrument.VoiceJoins.Add(1,
            new KeyValuePair<string, object?>("source", "webhook"));

        ChannelGrainInstrument.VoiceActiveUsers.Record(state.State.Users.Count);
    }

    public async Task<ChannelEntity> UpdateChannel(ChannelInput input)
    {
        var callerId = this.GetUserId();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), callerId, ArgonEntitlement.ManageChannels))
            throw new UnauthorizedAccessException("No permission to manage channels");

        await using var ctx = await context.CreateDbContextAsync();

        var channel = await ctx.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
        channel.Name        = input.Name;
        channel.Description = input.Description ?? channel.Description;
        channel.ChannelType = input.ChannelType;

        await ctx.SaveChangesAsync();
        _self = channel;
        return channel;
    }

    public async Task<Either<ChannelEntity, UpdateChannelError>> UpdateChannelSettings(string? name, string? description, int? slowModeSeconds,
        CancellationToken ct = default)
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

        await using var ctx = await context.CreateDbContextAsync(ct);

        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null)
            return UpdateChannelError.CHANNEL_NOT_FOUND;

        // The bag tells subscribers which fields to re-read; sending the whole channel back would
        // race with any concurrent reorder, which travels on its own event.
        var changed = new List<string>();

        if (name is not null && name != channel.Name)
        {
            channel.Name = name;
            changed.Add(nameof(ArgonChannel.name));
        }

        if (description is not null && description != channel.Description)
        {
            channel.Description = description;
            changed.Add(nameof(ArgonChannel.description));
        }

        if (slowModeSeconds is { } window)
        {
            var value = window == 0 ? null : (TimeSpan?)TimeSpan.FromSeconds(window);
            if (value != channel.SlowMode)
            {
                channel.SlowMode = value;
                changed.Add(nameof(ArgonChannel.slowModeSeconds));
            }
        }

        if (changed.Count == 0)
            return channel;

        await ctx.SaveChangesAsync(ct);

        // Refresh the activation's copy: SendMessage reads the cooldown off _self on every send, and
        // the write went through a detached context that the activation cannot see.
        _self = channel;

        await Fire(new ChannelModified(SpaceId, channelId, new IonArray<string>(changed)), ct);

        return channel;
    }

    public async Task<DeleteMessageError> DeleteMessage(long messageId, CancellationToken ct = default)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var message = await ctx.Messages
           .AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .Select(m => new { m.CreatorId })
           .FirstOrDefaultAsync(ct);

        if (message is null)
            return DeleteMessageError.MESSAGE_NOT_FOUND;

        // Retracting your own words needs no permission; taking down somebody else's is moderation.
        if (message.CreatorId != callerId
         && !await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageMessages, ct))
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

        await FireChannel(new MessageDeleted(SpaceId, channelId, messageId, callerId), ct);

        return DeleteMessageError.NONE;
    }

    /// <summary>
    /// Refuses a send that arrives inside the channel's cooldown. Slow mode is a tool moderators
    /// point at a room, not at themselves, so anyone holding <c>ManageMessages</c> — the same
    /// entitlement that lets them clean up afterwards — passes straight through.
    /// </summary>
    private async Task EnforceSlowModeAsync(Guid senderId, Guid channelId)
    {
        if (_self.SlowMode is not { } window || window <= TimeSpan.Zero)
            return;

        if (await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.ManageMessages))
            return;

        if (_lastSentBySender.TryGetValue(senderId, out var lastSentAt) && DateTimeOffset.UtcNow - lastSentAt < window)
            throw new InvalidOperationException("Slow mode is active in this channel");
    }

    private void NoteSent(Guid senderId)
    {
        if (_self.SlowMode is not { } window || window <= TimeSpan.Zero)
            return;

        // Everyone who ever posted here would otherwise stay in the dictionary for the lifetime of
        // the activation; entries older than the window can no longer block anyone, so drop them.
        if (_lastSentBySender.Count > 512)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            foreach (var stale in _lastSentBySender.Where(x => x.Value < cutoff).Select(x => x.Key).ToList())
                _lastSentBySender.Remove(stale);
        }

        _lastSentBySender[senderId] = DateTimeOffset.UtcNow;
    }

    public async Task<Either<string, VoiceInviteError>> CreateVoiceInvite(TimeSpan expiration, int maxUses, CancellationToken ct = default)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (_self.ChannelType != ChannelType.Voice)
            return VoiceInviteError.CHANNEL_IS_NOT_VOICE;

        // You cannot hand out a key to a room you cannot walk into yourself — Connect also implies
        // JoinToVoice and ViewChannel through the entitlement analyzer, so one check covers the path.
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.Connect, ct))
            return VoiceInviteError.INSUFFICIENT_PERMISSIONS;

        try
        {
            var code = await GrainFactory.GetGrain<IServerInvitesGrain>(SpaceId)
               .CreateInviteLinkAsync(callerId, expiration, maxUses, channelId);
            return code.inviteCode;
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to create voice invite for channel {ChannelId}", channelId);
            return VoiceInviteError.INTERNAL_ERROR;
        }
    }

    public async Task<List<ArgonMessageEntity>> QueryMessages(long? @from, int limit)
    {
        var messages = await messagesLayout.QueryMessages(_self.SpaceId, this.GetPrimaryKey(), @from, limit);
        await ResolveAttachmentUrls(messages);
        return messages;
    }

    public async Task<long> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo, List<ControlRowV1>? controls = null)
    {
        if (_self.ChannelType != ChannelType.Text) throw new InvalidOperationException("Channel is not text");

        if (controls is { Count: > 0 })
            ControlRowV1.ValidateRows(controls);
        
        var sw = Stopwatch.StartNew();
        var senderId = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        await EnforceSlowModeAsync(senderId, channelId);

        if (entities is { Count: > 0 } && entities.Any(e => e is MessageEntityAttachment or MessageEntityGif))
        {
            if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.AttachFiles))
                throw new InvalidOperationException("User does not have AttachFiles permission");

            var attachmentCount = entities.Count(e => e is MessageEntityAttachment or MessageEntityGif);
            if (attachmentCount > 10)
                throw new InvalidOperationException("Maximum 10 attachments per message");
        }
        
        logger.LogInformation(
            "SendMessage called: ChannelId={ChannelId}, SenderId={SenderId}, TextLength={TextLength}, EntitiesCount={EntitiesCount}, RandomId={RandomId}, ReplyTo={ReplyTo}",
            channelId, senderId, text?.Length ?? 0, entities?.Count ?? 0, randomId, replyTo);
        
        if (entities is { Count: > 0 })
        {
            logger.LogDebug("Input entities types: {EntityTypes}", 
                string.Join(", ", entities.Select((e, i) => $"[{i}]={e.GetType().Name}")));
        }
        
        var sanitized = SanitizeEntities(entities ?? []);
        await CacheGifEntitiesAsync(sanitized, senderId);

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

        logger.LogInformation(
            "Created ArgonMessageEntity: SpaceId={SpaceId}, ChannelId={ChannelId}, EntitiesCount={EntitiesCount}, EntitiesIsNull={EntitiesIsNull}",
            message.SpaceId, message.ChannelId, message.Entities?.Count ?? 0, message.Entities == null);

        var dup = await messagesLayout.CheckDuplicationAsync(message, randomId);

        if (dup is not null)
        {
            sw.Stop();
            logger.LogInformation("Duplicate message detected, returning existing MessageId={MessageId}", dup.Value);
            return dup.Value;
        }

        var msgId = await messagesLayout.ExecuteInsertMessage(message, randomId);

        // Only a message that actually landed starts the next cooldown — a retry that de-duplicated
        // above returned earlier and must not push the author's window forward.
        NoteSent(senderId);

        message.MessageId = msgId;

        logger.LogInformation(
            "Message inserted with MessageId={MessageId}, EntitiesCount={EntitiesCount}",
            msgId, message.Entities?.Count ?? 0);

        var dto = message.ToDto();

        logger.LogInformation(
            "Message DTO created: MessageId={MessageId}, EntitiesSize={EntitiesSize}",
            dto.messageId, dto.entities.Size);

        if (dto.entities.Size > 0)
        {
            var entityTypes = dto.entities.Values.Select((e, i) => $"[{i}]={e?.GetType().Name ?? "null"}");
            logger.LogInformation("DTO entities types: {EntityTypes}", string.Join(", ", entityTypes));
        }
        else
        {
            logger.LogWarning(
                "DTO entities are empty after ToDto() conversion! Original EntitiesCount was {OriginalCount}",
                message.Entities?.Count ?? 0);
        }

        await ResolveAttachmentUrls(message);
        dto = message.ToDto();

        // MessageSent stays SPACE-scoped (for now): clients derive unread badges for channels they
        // are NOT currently viewing from this event. Channel-scoping it needs the space-size gate
        // (large spaces → channel-scoped + pull-based unread; small spaces → space-scoped + live
        // unread), unlike typing/reactions/edits which have no cross-channel consumer.
        await Fire(new MessageSent(_self.SpaceId, dto));

        // Update channel LastMessageId
        _ = UpdateLastMessageIdAsync(msgId);

        // Process mentions asynchronously (don't block message delivery)
        _ = ProcessMentionsAsync(entities, msgId, senderId, replyTo);
        
        sw.Stop();
        
        ChannelGrainInstrument.MessagesSent.Add(1,
            new KeyValuePair<string, object?>("channel_type", "text"));
        
        ChannelGrainInstrument.MessageSendDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("channel_type", "text"),
            new KeyValuePair<string, object?>("has_reply", replyTo.HasValue ? "true" : "false"));
        
        logger.LogInformation("MessageSent event fired for MessageId={MessageId}", msgId);

        // Track message sent for stats
        _ = TrackMessageSentAsync(senderId);

        return msgId;
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

    private async Task UpdateLastMessageIdAsync(long messageId)
    {
        try
        {
            await using var ctx = await context.CreateDbContextAsync();
            await ctx.Channels
                .Where(c => c.Id == this.GetPrimaryKey())
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageId, messageId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update LastMessageId for channel {ChannelId}", this.GetPrimaryKey());
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

    private async Task ProcessMentionsAsync(List<IMessageEntity>? entities, long messageId, Guid senderId, long? replyTo)
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
                    .Where(m => m.SpaceId == _self.SpaceId && m.ChannelId == this.GetPrimaryKey() && m.MessageId == replyTo.Value)
                    .Select(m => m.CreatorId)
                    .FirstOrDefaultAsync();

                if (originalAuthor != default && originalAuthor != senderId)
                {
                    await readStateService.IncrementMentionsAsync(originalAuthor, this.GetPrimaryKey(), _self.SpaceId, 1);
                }
            }

            if (entities is null or { Count: 0 }) return;

            var userMentions = entities.OfType<MessageEntityMention>().ToList();
            foreach (var mention in userMentions)
            {
                if (mention.userId == senderId) continue;
                await readStateService.IncrementMentionsAsync(mention.userId, this.GetPrimaryKey(), _self.SpaceId, 1);
            }

            var hasEveryoneMention = entities.OfType<MessageEntityMentionEveryone>().Any();
            var roleMentions = entities.OfType<MessageEntityMentionRole>().ToList();

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

        return await ctx.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
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
        var interactionId = Guid.NewGuid();

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
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId)
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
        var interactionId = Guid.NewGuid();
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
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId)
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

        var interactionId = Guid.NewGuid();
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

        var ctx = botEventPublisher.InteractionStore.TryConsume(interactionId);
        if (ctx is null)
            return new FailedSubmitModal(SubmitModalError.INTERACTION_EXPIRED);

        if (ctx.UserId != senderId)
            return new FailedSubmitModal(SubmitModalError.INTERACTION_NOT_FOUND);

        var user = await botUserCache.GetOrResolveAsync(senderId);

        var customId = interactionId.ToString();
        var mappedValues = values
           .Select(v => new ModalSubmitValueV1(v.customId, [v.value]))
           .ToList();

        await botEventPublisher.PublishModalSubmitAsync(
            Guid.NewGuid(), customId, channelId, SpaceId, user, mappedValues);

        return new SuccessSubmitModal();
    }

    public async Task EditBotMessage(long messageId, Guid botUserId, string? text, List<ControlRowV1>? controls)
    {
        if (controls is { Count: > 0 })
            ControlRowV1.ValidateRows(controls);

        var channelId = this.GetPrimaryKey();
        await using var ctx = await context.CreateDbContextAsync();

        var message = await ctx.Messages
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && m.CreatorId == botUserId)
           .FirstOrDefaultAsync();

        if (message is null)
            throw new InvalidOperationException("Message not found or not owned by this bot.");

        if (text is not null)
            message.Text = text;

        if (controls is not null)
            message.Controls = controls.Count == 0 ? null : controls;

        message.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();

        await FireChannel(new MessageEdited(SpaceId, channelId, messageId, message.Text, message.UpdatedAt.UtcDateTime));
    }

    // ── Reactions (buffered writes) ──────────────────────────

    public async Task<IAddReactionResult> AddReaction(long messageId, string emoji)
    {
        if (_self.ChannelType != ChannelType.Text)
        {
            ChannelGrainInstrument.ReactionsAdded.Add(1,
                new KeyValuePair<string, object?>("result", "invalid_channel"));
            return new FailedAddReaction(AddReactionError.NONE);
        }

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
               .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && uncachedIds.Contains(m.MessageId))
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
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == this.GetPrimaryKey() && m.MessageId == messageId)
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

    private async Task FlushReactionsAsync()
    {
        if (_dirtyReactions.Count == 0)
            return;

        var toFlush = _dirtyReactions.ToList();
        _dirtyReactions.Clear();

        await using var ctx = await context.CreateDbContextAsync();
        var channelId = this.GetPrimaryKey();

        foreach (var messageId in toFlush)
        {
            if (!_reactionCache.TryGetValue(messageId, out var reactions))
                continue;

            var json = reactions.Count == 0
                ? null
                : Newtonsoft.Json.JsonConvert.SerializeObject(reactions);

            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Messages\" SET \"Reactions\" = {json}::jsonb WHERE \"SpaceId\" = {SpaceId} AND \"ChannelId\" = {channelId} AND \"MessageId\" = {messageId}");
        }
    }
}