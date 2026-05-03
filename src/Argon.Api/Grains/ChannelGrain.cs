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
using Core.Entities.Data;

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

    private Task Fire<T>(T ev, CancellationToken ct = default) where T : IArgonEvent
        => appHubServer.BroadcastSpace(ev, SpaceId, ct);

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

    public async Task<ChannelRealtimeState> GetRealtimeStateAsync(CancellationToken ct = default)
    {
        var members = state.State.Users.Select(x => x.Value).ToList();
        
        LinkedMeetingInfo? meetInfo = null;
        if (state.State.LinkedMeetId.HasValue && !string.IsNullOrEmpty(state.State.LinkedMeetInviteCode))
        {
            var meetId = state.State.LinkedMeetId.Value;
            var inviteCode = state.State.LinkedMeetInviteCode;
            
            var meetGrain = this.GrainFactory.GetGrain<IMeetingGrain>(meetId.ToString());
            var meetState = await meetGrain.GetStateAsync(ct);
            
            if (meetState is not null && !meetState.IsEnded)
            {
                meetInfo = new LinkedMeetingInfo(
                    meetId,
                    $"https://meet.argon.gl/i/{inviteCode}",
                    inviteCode,
                    meetState.CreatedAt.UtcDateTime);
            }
            else
            {
                // Meeting ended, clear the link
                state.State.LinkedMeetId = null;
                state.State.LinkedMeetInviteCode = null;
                await state.WriteStateAsync(ct);
            }
        }
        
        return new ChannelRealtimeState(members, meetInfo);
    }

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
        
        await Fire(new UserTypingEvent(SpaceId, ChannelId.ShardId, this.GetUserId(), null));
    }

    [OneWay]
    public async ValueTask OnTypingStopEmit()
    {
        ChannelGrainInstrument.TypingEvents.Add(1,
            new KeyValuePair<string, object?>("event_type", "stop_typing"));
        
        await Fire(new UserStopTypingEvent(SpaceId, ChannelId.ShardId, this.GetUserId()));
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

        await Fire(new UserTypingEvent(SpaceId, channelId, userId, kind));

        // Register auto-stop timer — fires UserStopTypingEvent after timeout
        _botTypingTimers[userId] = this.RegisterGrainTimer(async _ =>
        {
            _botTypingTimers.Remove(userId);
            await Fire(new UserStopTypingEvent(SpaceId, channelId, userId));
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

    public async Task<ChannelMeetingResult?> CreateLinkedMeetingAsync(CancellationToken ct = default)
    {
        var channelId = this.GetPrimaryKey();
        
        if (_self.ChannelType != ChannelType.Voice)
        {
            ChannelGrainInstrument.LinkedMeetingsCreated.Add(1,
                new KeyValuePair<string, object?>("result", "error"));
            
            logger.LogWarning("Cannot create linked meeting for non-voice channel {ChannelId}", channelId);
            return null;
        }

        var userId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Check if user has permission to create meetings
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, userId, ArgonEntitlement.ManageChannels, ct))
        {
            ChannelGrainInstrument.LinkedMeetingsCreated.Add(1,
                new KeyValuePair<string, object?>("result", "no_permission"));
            
            logger.LogWarning("User {UserId} lacks permission to create linked meeting for channel {ChannelId}", 
                userId, channelId);
            return null;
        }

        // If there's already a linked meeting, return its info
        if (state.State.LinkedMeetId.HasValue && !string.IsNullOrEmpty(state.State.LinkedMeetInviteCode))
        {
            var existingMeetGrain = this.GrainFactory.GetGrain<IMeetingGrain>(state.State.LinkedMeetId.Value.ToString());
            var existingState = await existingMeetGrain.GetStateAsync(ct);
            
            // If meeting is still active, return it
            if (existingState is { IsEnded: false })
            {
                ChannelGrainInstrument.LinkedMeetingsCreated.Add(1,
                    new KeyValuePair<string, object?>("result", "already_exists"));
                
                logger.LogInformation("Returning existing linked meeting {MeetId} for channel {ChannelId}", 
                    state.State.LinkedMeetId.Value, channelId);
                return new ChannelMeetingResult(
                    state.State.LinkedMeetId.Value,
                    state.State.LinkedMeetInviteCode,
                    $"https://meet.argon.gl/i/{state.State.LinkedMeetInviteCode}");
            }
            
            // Meeting ended, clear the link
            logger.LogInformation("Previous linked meeting {MeetId} has ended, clearing link for channel {ChannelId}", 
                state.State.LinkedMeetId.Value, channelId);
            state.State.LinkedMeetId = null;
            state.State.LinkedMeetInviteCode = null;
        }

        // Get user info for host
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            ChannelGrainInstrument.LinkedMeetingsCreated.Add(1,
                new KeyValuePair<string, object?>("result", "error"));
            
            logger.LogWarning("User {UserId} not found when creating linked meeting for channel {ChannelId}", 
                userId, channelId);
            return null;
        }

        var meetId = Guid.CreateVersion7();
        logger.LogInformation("Creating linked meeting {MeetId} for channel {ChannelId} in space {SpaceId} with host {HostId}", 
            meetId, channelId, SpaceId, userId);

        var meetGrain = this.GrainFactory.GetGrain<IMeetingGrain>(meetId.ToString());

        var result = await meetGrain.CreateLinkedAsync(
            SpaceId,
            channelId,
            userId,
            user.DisplayName ?? user.Username,
            user.AvatarFileId,
            ct);

        // Add host to channel voice (done here to avoid deadlock - MeetingGrain can't call back to ChannelGrain)
        await JoinFromMeetingInternalAsync(userId, user.DisplayName ?? user.Username, isGuest: false, ct);

        // Register invite code mapping - normalize by removing dashes and uppercasing
        var normalizedCode = result.InviteCode.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        logger.LogDebug("Registering invite code {InviteCode} (normalized: {NormalizedCode}) for meeting {MeetId}", 
            result.InviteCode, normalizedCode, meetId);
        var inviteGrain = this.GrainFactory.GetGrain<IInviteCodeGrain>(normalizedCode);
        await inviteGrain.RegisterAsync(meetId, ct);

        // Store the link
        state.State.LinkedMeetId = meetId;
        state.State.LinkedMeetInviteCode = result.InviteCode;
        await state.WriteStateAsync(ct);

        var meetUrl = $"https://meet.argon.gl/i/{result.InviteCode}";
        var meetInfo = new LinkedMeetingInfo(meetId, meetUrl, result.InviteCode, DateTime.UtcNow);

        // Fire event to notify all subscribers
        await Fire(new MeetingCreatedFor(SpaceId, channelId, meetInfo), ct);

        ChannelGrainInstrument.LinkedMeetingsCreated.Add(1,
            new KeyValuePair<string, object?>("result", "success"));

        logger.LogInformation("Created linked meeting {MeetId} with invite code {InviteCode} for channel {ChannelId} in space {SpaceId} by user {UserId}",
            meetId, result.InviteCode, channelId, SpaceId, userId);

        return new ChannelMeetingResult(meetId, result.InviteCode, meetUrl);
    }

    public Task<string?> GetMeetingLinkAsync(CancellationToken ct = default)
    {
        if (!state.State.LinkedMeetId.HasValue || string.IsNullOrEmpty(state.State.LinkedMeetInviteCode))
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>($"https://meet.argon.gl/i/{state.State.LinkedMeetInviteCode}");
    }

    public async Task<LinkedMeetingInfo?> GetLinkedMeetingInfoAsync(CancellationToken ct = default)
    {
        if (!state.State.LinkedMeetId.HasValue || string.IsNullOrEmpty(state.State.LinkedMeetInviteCode))
            return null;

        var meetId = state.State.LinkedMeetId.Value;
        var inviteCode = state.State.LinkedMeetInviteCode;

        // Check if meeting is still active
        var meetGrain = this.GrainFactory.GetGrain<IMeetingGrain>(meetId.ToString());
        var meetState = await meetGrain.GetStateAsync(ct);

        if (meetState is null || meetState.IsEnded)
        {
            // Meeting ended, clear the link
            state.State.LinkedMeetId = null;
            state.State.LinkedMeetInviteCode = null;
            await state.WriteStateAsync(ct);
            return null;
        }

        return new LinkedMeetingInfo(
            meetId,
            $"https://meet.argon.gl/i/{inviteCode}",
            inviteCode,
            meetState.CreatedAt.UtcDateTime);
    }

    public async Task<bool> EndLinkedMeetingAsync(CancellationToken ct = default)
    {
        if (!state.State.LinkedMeetId.HasValue)
        {
            ChannelGrainInstrument.LinkedMeetingsEnded.Add(1,
                new KeyValuePair<string, object?>("result", "not_found"));
            return false;
        }

        var userId = this.GetUserId();
        var meetId = state.State.LinkedMeetId.Value;
        var inviteCode = state.State.LinkedMeetInviteCode!;

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Check if user has permission
        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, this.GetPrimaryKey(), userId, ArgonEntitlement.ManageChannels, ct))
        {
            ChannelGrainInstrument.LinkedMeetingsEnded.Add(1,
                new KeyValuePair<string, object?>("result", "no_permission"));
            return false;
        }

        var meetGrain = this.GrainFactory.GetGrain<IMeetingGrain>(meetId.ToString());
        var meetState = await meetGrain.GetStateAsync(ct);
        var ended = await meetGrain.EndMeetingAsync(userId, ct);

        if (ended)
        {
            state.State.LinkedMeetId = null;
            state.State.LinkedMeetInviteCode = null;
            await state.WriteStateAsync(ct);

            var meetUrl = $"https://meet.argon.gl/i/{inviteCode}";
            var meetInfo = new LinkedMeetingInfo(
                meetId, 
                meetUrl, 
                inviteCode, 
                meetState?.CreatedAt.UtcDateTime ?? DateTime.UtcNow);

            // Fire event to notify all subscribers
            await Fire(new MeetingDeletedFor(SpaceId, this.GetPrimaryKey(), meetInfo), ct);

            ChannelGrainInstrument.LinkedMeetingsEnded.Add(1,
                new KeyValuePair<string, object?>("result", "success"));

            logger.LogInformation("Ended linked meeting for channel {ChannelId} in space {SpaceId}",
                this.GetPrimaryKey(), SpaceId);
        }

        return ended;
    }

    /// <summary>
    /// Prefix for ephemeral guest user IDs from meetings.
    /// </summary>
    private static readonly byte[] GuestIdPrefix = [0xFA, 0xFC, 0xCC, 0xCC];

    private static bool IsGuestUserId(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);
        return bytes[..4].SequenceEqual(GuestIdPrefix);
    }

    public Task JoinFromMeetingAsync(Guid oderId, string displayName, bool isGuest, CancellationToken ct = default)
        => JoinFromMeetingInternalAsync(oderId, displayName, isGuest, ct);

    private async Task JoinFromMeetingInternalAsync(Guid oderId, string displayName, bool isGuest, CancellationToken ct = default)
    {
        if (_self.ChannelType != ChannelType.Voice)
        {
            logger.LogWarning("Cannot join from meeting to non-voice channel {ChannelId}", this.GetPrimaryKey());
            return;
        }

        var channelId = this.GetPrimaryKey();

        // If user already in channel, handle rejoin
        if (state.State.Users.ContainsKey(oderId))
        {
            logger.LogDebug("User {UserId} already in channel {ChannelId}, skipping join from meeting", oderId, channelId);
            return;
        }

        // For non-guests, check if they have existing join time and handle it
        if (!isGuest && state.State.UserJoinTimes.TryGetValue(oderId, out _))
        {
            await SettleXpForAllUsersAsync();
            state.State.UserJoinTimes.Remove(oderId);
            state.State.Users.Remove(oderId);
            await Fire(new LeavedFromChannelUser(SpaceId, channelId, oderId), ct);
            await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserLeftVoiceAsync(oderId);
        }

        // Settle XP for existing users before adding new one
        await SettleXpForAllUsersAsync();

        // Add user to channel
        state.State.Users[oderId] = new RealtimeChannelUser(oderId, ChannelMemberState.NONE);

        // Only track join times for non-guests (for stats)
        if (!isGuest)
        {
            state.State.UserJoinTimes[oderId] = DateTimeOffset.UtcNow;
            _ = TrackCallJoinedAsync(oderId);
        }

        await state.WriteStateAsync(ct);
        await Fire(new JoinedToChannelUser(SpaceId, channelId, oderId), ct);
        await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserJoinedVoiceAsync(oderId, channelId, DateTimeOffset.UtcNow);

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        ChannelGrainInstrument.VoiceJoins.Add(1,
            new KeyValuePair<string, object?>("source", "meeting"));
        
        ChannelGrainInstrument.VoiceActiveUsers.Record(state.State.Users.Count);

        logger.LogInformation("User {UserId} ({DisplayName}) joined channel {ChannelId} from meeting (guest: {IsGuest})",
            oderId, displayName, channelId, isGuest);
    }

    public async Task LeaveFromMeetingAsync(Guid oderId, CancellationToken ct = default)
    {
        var channelId = this.GetPrimaryKey();
        var isGuest = IsGuestUserId(oderId);

        if (!state.State.Users.ContainsKey(oderId))
        {
            logger.LogDebug("User {UserId} not in channel {ChannelId}, skipping leave from meeting", oderId, channelId);
            return;
        }

        // Settle XP for ALL users (including the one leaving) before removing
        await SettleXpForAllUsersAsync();
        
        // Only record total session duration for metrics (not for XP, that's handled by settle)
        if (!isGuest && state.State.UserJoinTimes.TryGetValue(oderId, out var joinTime))
        {
            var duration = DateTimeOffset.UtcNow - joinTime;
            ChannelGrainInstrument.VoiceSessionDuration.Record(duration.TotalSeconds);
            state.State.UserJoinTimes.Remove(oderId);
        }

        state.State.Users.Remove(oderId);
        await Fire(new LeavedFromChannelUser(SpaceId, channelId, oderId), ct);
        await this.GrainFactory.GetGrain<ISpaceGrain>(SpaceId).OnUserLeftVoiceAsync(oderId);
        await state.WriteStateAsync(ct);

        if (state.State.Users.Count == 0)
            this.DelayDeactivation(TimeSpan.MinValue);

        ChannelGrainInstrument.VoiceLeaves.Add(1,
            new KeyValuePair<string, object?>("source", "meeting"));
        
        ChannelGrainInstrument.VoiceActiveUsers.Record(state.State.Users.Count);

        logger.LogInformation("User {UserId} left channel {ChannelId} from meeting (guest: {IsGuest})",
            oderId, channelId, isGuest);
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
        return channel;
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

        if (entities is { Count: > 0 } && entities.Any(e => e is MessageEntityAttachment))
        {
            if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, senderId, ArgonEntitlement.AttachFiles))
                throw new InvalidOperationException("User does not have AttachFiles permission");

            var attachmentCount = entities.Count(e => e is MessageEntityAttachment);
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
        
        var message = new ArgonMessageEntity
        {
            SpaceId   = _self.SpaceId,
            ChannelId = channelId,
            CreatorId = senderId,
            Entities  = entities ?? [],
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

    private async Task ResolveAttachmentUrls(List<ArgonMessageEntity> messages)
    {
        var fileIds = messages
           .SelectMany(m => m.Entities ?? [])
           .OfType<MessageEntityAttachment>()
           .Where(a => a.downloadUrl is null)
           .Select(a => a.fileId)
           .Distinct()
           .ToList();

        if (fileIds.Count == 0) return;

        await using var db = await context.CreateDbContextAsync();
        var files = await db.Files
           .Where(f => fileIds.Contains(f.Id) && f.Finalized)
           .Select(f => new { f.Id, f.S3Key })
           .ToListAsync();

        var urlMap = files.ToDictionary(f => f.Id, f => s3.GetDownloadUrl(f.S3Key));

        foreach (var message in messages)
        {
            if (message.Entities is not { Count: > 0 }) continue;
            for (var i = 0; i < message.Entities.Count; i++)
            {
                if (message.Entities[i] is MessageEntityAttachment { downloadUrl: null } att &&
                    urlMap.TryGetValue(att.fileId, out var url))
                {
                    message.Entities[i] = att with { downloadUrl = url };
                }
            }
        }
    }

    private async Task ResolveAttachmentUrls(ArgonMessageEntity message)
    {
        if (message.Entities is not { Count: > 0 }) return;

        var attachments = message.Entities.OfType<MessageEntityAttachment>()
           .Where(a => a.downloadUrl is null)
           .ToList();

        if (attachments.Count == 0) return;

        var fileIds = attachments.Select(a => a.fileId).Distinct().ToList();

        await using var db = await context.CreateDbContextAsync();
        var files = await db.Files
           .Where(f => fileIds.Contains(f.Id) && f.Finalized)
           .Select(f => new { f.Id, f.S3Key })
           .ToListAsync();

        var urlMap = files.ToDictionary(f => f.Id, f => s3.GetDownloadUrl(f.S3Key));

        for (var i = 0; i < message.Entities.Count; i++)
        {
            if (message.Entities[i] is MessageEntityAttachment { downloadUrl: null } att &&
                urlMap.TryGetValue(att.fileId, out var url))
            {
                message.Entities[i] = att with { downloadUrl = url };
            }
        }
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
                    var allMembers = await ctx.UsersToServerRelations
                        .Where(m => m.SpaceId == _self.SpaceId && m.UserId != senderId)
                        .Select(m => m.UserId)
                        .ToListAsync();

                    var mutedUsers = await muteService.FilterMutedUsersAsync(this.GetPrimaryKey(), _self.SpaceId, allMembers);
                    var suppressUsers = await ctx.Set<MuteSettingsEntity>()
                        .Where(m => allMembers.Contains(m.UserId) && m.SuppressEveryone && (m.TargetId == _self.SpaceId || m.TargetId == this.GetPrimaryKey()))
                        .Select(m => m.UserId)
                        .Distinct()
                        .ToListAsync();

                    var targetUsers = allMembers
                        .Where(u => !mutedUsers.Contains(u) && !suppressUsers.Contains(u))
                        .ToList();

                    await readStateService.BatchIncrementMentionsAsync(_self.SpaceId, this.GetPrimaryKey(), targetUsers);

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
            fileInfo.IsPublic ? null : fileInfo.DownloadUrl);
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

        await Fire(new MessageEdited(SpaceId, channelId, messageId, message.Text, message.UpdatedAt.UtcDateTime));
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

        await Fire(new ReactionAdded(SpaceId, channelId, messageId, userId, emoji, null));

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

        await Fire(new ReactionRemoved(SpaceId, channelId, messageId, userId, emoji));

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