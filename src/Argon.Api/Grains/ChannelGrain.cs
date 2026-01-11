namespace Argon.Grains;

using Api.Features.CoreLogic.Messages;
using Argon.Api.Features.Bus;
using Core.Grains.Interfaces;
using Core.Services;
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
    IDbContextFactory<ApplicationDbContext> context,
    IMessagesLayout messagesLayout,
    IEntitlementChecker entitlementChecker,
    ILogger<ChannelGrain> logger) : Grain, IChannelGrain
{
    private IDistributedArgonStream<IArgonEvent> _userStateEmitter = null!;

    private ChannelEntity _self     { get; set; }
    private Guid          SpaceId   => _self.SpaceId;
    private ArgonRoomId   ChannelId => new(SpaceId, this.GetPrimaryKey());

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _self = await Get();

        _userStateEmitter = await this.Streams().CreateServerStreamFor(SpaceId);

        await state.ReadStateAsync(cancellationToken);

        state.State.Users.Clear();
        state.State.UserJoinTimes.Clear();
        state.State.EgressActive = false;

        await state.WriteStateAsync(cancellationToken);
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Record voice time for all users still in channel
        foreach (var (userId, joinTime) in state.State.UserJoinTimes)
        {
            await RecordVoiceTimeForUserAsync(userId, joinTime);
        }

        await Task.WhenAll(state.State.Users.Select(x => Leave(x.Key)));
        await _userStateEmitter.DisposeAsync();
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
        => await _userStateEmitter.Fire(new UserTypingEvent(SpaceId, ChannelId.ShardId, this.GetUserId()));

    [OneWay]
    public async ValueTask OnTypingStopEmit()
        => await _userStateEmitter.Fire(new UserStopTypingEvent(SpaceId, ChannelId.ShardId, this.GetUserId()));

    public async Task<bool> KickMemberFromChannel(Guid memberId)
    {
        if (_self.ChannelType != ChannelType.Voice)
            return false;

        await using var ctx = await context.CreateDbContextAsync();

        var userId = this.GetUserId();

        if (!await entitlementChecker.HasAccessAsync(ctx, SpaceId, userId, ArgonEntitlement.KickMember))
            return false;

        return await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty)
           .KickParticipantAsync(new ArgonUserId(memberId), new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()));
    }

    public async Task<bool> BeginRecord(CancellationToken ct = default)
    {
        if (state.State.EgressActive)
            return false;

        var result = await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty)
           .BeginRecordAsync(new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()), ct);

        await _userStateEmitter.Fire(new RecordStarted(this.SpaceId, this.GetPrimaryKey(), this.GetUserId()), ct);

        state.State.EgressActive      = true;
        state.State.EgressId          = result;
        state.State.UserCreatedEgress = this.GetUserId();

        return true;
    }

    public async Task<bool> StopRecord(CancellationToken ct = default)
    {
        if (!state.State.EgressActive)
            return false;
        var egressId = state.State.EgressId;
        await _userStateEmitter.Fire(new RecordEnded(this.SpaceId, this.GetPrimaryKey()), ct);
        state.State.EgressActive      = false;
        state.State.EgressId          = null;
        state.State.UserCreatedEgress = null;
        var result = await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty)
           .StopRecordAsync(new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()), egressId!, ct);
        return result;
    }

    public async Task<ChannelMeetingResult?> CreateLinkedMeetingAsync(CancellationToken ct = default)
    {
        var channelId = this.GetPrimaryKey();
        
        if (_self.ChannelType != ChannelType.Voice)
        {
            logger.LogWarning("Cannot create linked meeting for non-voice channel {ChannelId}", channelId);
            return null;
        }

        var userId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Check if user has permission to create meetings
        if (!await entitlementChecker.HasAccessAsync(ctx, SpaceId, userId, ArgonEntitlement.ManageChannels, ct))
        {
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
        await _userStateEmitter.Fire(new MeetingCreatedFor(SpaceId, channelId, meetInfo), ct);

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
            return false;

        var userId = this.GetUserId();
        var meetId = state.State.LinkedMeetId.Value;
        var inviteCode = state.State.LinkedMeetInviteCode!;

        await using var ctx = await context.CreateDbContextAsync(ct);

        // Check if user has permission
        if (!await entitlementChecker.HasAccessAsync(ctx, SpaceId, userId, ArgonEntitlement.ManageChannels, ct))
            return false;

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
            await _userStateEmitter.Fire(new MeetingDeletedFor(SpaceId, this.GetPrimaryKey(), meetInfo), ct);

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

    public async Task JoinFromMeetingAsync(Guid oderId, string displayName, bool isGuest, CancellationToken ct = default)
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
        if (!isGuest && state.State.UserJoinTimes.TryGetValue(oderId, out var existingJoinTime))
        {
            await RecordVoiceTimeForUserAsync(oderId, existingJoinTime);
            state.State.UserJoinTimes.Remove(oderId);
            state.State.Users.Remove(oderId);
            await _userStateEmitter.Fire(new LeavedFromChannelUser(SpaceId, channelId, oderId), ct);
        }

        // Add user to channel
        state.State.Users[oderId] = new RealtimeChannelUser(oderId, ChannelMemberState.NONE);

        // Only track join times for non-guests (for stats)
        if (!isGuest)
        {
            state.State.UserJoinTimes[oderId] = DateTimeOffset.UtcNow;
            _ = TrackCallJoinedAsync(oderId);
        }

        await state.WriteStateAsync(ct);
        await _userStateEmitter.Fire(new JoinedToChannelUser(SpaceId, channelId, oderId), ct);

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        logger.LogInformation("User {UserId} ({DisplayName}) joined channel {ChannelId} from meeting (guest: {IsGuest})",
            oderId, displayName, channelId, isGuest);
    }

    public async Task LeaveFromMeetingAsync(Guid oderId, CancellationToken ct = default)
    {
        var channelId = this.GetPrimaryKey();
        var isGuest = IsGuestUserId(oderId);

        if (!state.State.Users.Remove(oderId))
        {
            logger.LogDebug("User {UserId} not in channel {ChannelId}, skipping leave from meeting", oderId, channelId);
            return;
        }

        // Only record voice time for non-guests
        if (!isGuest && state.State.UserJoinTimes.TryGetValue(oderId, out var joinTime))
        {
            await RecordVoiceTimeForUserAsync(oderId, joinTime);
            state.State.UserJoinTimes.Remove(oderId);
        }

        await _userStateEmitter.Fire(new LeavedFromChannelUser(SpaceId, channelId, oderId), ct);
        await state.WriteStateAsync(ct);

        if (state.State.Users.Count == 0)
            this.DelayDeactivation(TimeSpan.MinValue);

        logger.LogInformation("User {UserId} left channel {ChannelId} from meeting (guest: {IsGuest})",
            oderId, channelId, isGuest);
    }

    public async Task<Either<string, JoinToChannelError>> Join()
    {
        if (_self.ChannelType != ChannelType.Voice)
            return JoinToChannelError.CHANNEL_IS_NOT_VOICE;

        var userId = this.GetUserId();

        if (state.State.UserJoinTimes.TryGetValue(userId, out var joinTime))
        {
            await RecordVoiceTimeForUserAsync(userId, joinTime);
            state.State.UserJoinTimes.Remove(userId);
            state.State.Users.Remove(userId);
            await _userStateEmitter.Fire(new LeavedFromChannelUser(SpaceId, this.GetPrimaryKey(), userId));
        }


        state.State.Users.Add(userId, new RealtimeChannelUser(userId, ChannelMemberState.NONE));
        state.State.UserJoinTimes[userId] = DateTimeOffset.UtcNow;
        await state.WriteStateAsync();

        // Track call joined for stats
        _ = TrackCallJoinedAsync(userId);

        await _userStateEmitter.Fire(new JoinedToChannelUser(SpaceId, this.GetPrimaryKey(), userId));

        if (state.State.Users.Count > 0)
            this.DelayDeactivation(TimeSpan.FromDays(1));

        return await this.GrainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty).IssueAuthorizationTokenAsync(new ArgonUserId(userId),
            new ArgonRoomId(this.SpaceId, this.GetPrimaryKey()), SfuPermissionKind.DefaultUser);
    }

    public async Task Leave(Guid userId)
    {
        // Calculate and record voice time before removing user
        if (state.State.UserJoinTimes.TryGetValue(userId, out var joinTime))
        {
            await RecordVoiceTimeForUserAsync(userId, joinTime);
            state.State.UserJoinTimes.Remove(userId);
        }

        state.State.Users.Remove(userId);
        await _userStateEmitter.Fire(new LeavedFromChannelUser(SpaceId, this.GetPrimaryKey(), userId));
        await state.WriteStateAsync();

        if (state.State.Users.Count == 0)
            this.DelayDeactivation(TimeSpan.MinValue);
    }

    public async Task<ChannelEntity> UpdateChannel(ChannelInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var channel = await ctx.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
        channel.Name        = input.Name;
        channel.Description = input.Description ?? channel.Description;
        channel.ChannelType = input.ChannelType;
        
        await ctx.SaveChangesAsync();
        return channel;
    }

    public async Task<List<ArgonMessageEntity>> QueryMessages(long? @from, int limit)
        => await messagesLayout.QueryMessages(_self.SpaceId, this.GetPrimaryKey(), @from, limit);

    public async Task<long> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo)
    {
        if (_self.ChannelType != ChannelType.Text) throw new InvalidOperationException("Channel is not text");
        
        var senderId = this.GetUserId();
        var channelId = this.GetPrimaryKey();
        
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
            Text      = text,
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

        await _userStateEmitter.Fire(new MessageSent(_self.SpaceId, dto));
        
        logger.LogInformation("MessageSent event fired for MessageId={MessageId}", msgId);

        // Track message sent for stats
        _ = TrackMessageSentAsync(senderId);

        return msgId;
    }

    private async Task RecordVoiceTimeForUserAsync(Guid userId, DateTimeOffset joinTime)
    {
        var duration = DateTimeOffset.UtcNow - joinTime;
        var durationSeconds = (int)Math.Min(duration.TotalSeconds, int.MaxValue);

        if (durationSeconds > 0)
        {
            var statsGrain = GrainFactory.GetGrain<IUserStatsGrain>(userId);
            await statsGrain.RecordVoiceTimeAsync(durationSeconds, this.GetPrimaryKey(), SpaceId);
        }
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

    private async Task<ChannelEntity> Get()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Channels.FirstAsync(c => c.Id == this.GetPrimaryKey());
    }
}