namespace Argon.Grains;

using Argon.Core.Grains.Interfaces;
using Argon.Features.Orleanse.Storages;
using Core.Services;
using Sfu;

public class VoiceBroadcastGrain(
    // Stores nothing; it is here so the runtime carries the links across a migration. See VolatileGrainStorage.
    [PersistentState("activation", VolatileGrainStorage.ProviderName)]
    IPersistentState<VoiceBroadcastActivationState> activation,
    IGrainFactory grainFactory,
    IEntitlementChecker entitlementChecker,
    IOptions<CallKitOptions> callKit,
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IVoiceBroadcastGrain> logger) : Grain, IVoiceBroadcastGrain
{
    /// <summary>How often the radio room is checked while anyone holds a link or a radio participant is in it.</summary>
    public static TimeSpan SweepPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How far past <c>MaxTransmitSeconds</c> a transmission runs before the server mutes it; the client releases first.</summary>
    public static TimeSpan TransmitGrace { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TokenReuse          = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ForwardRetryDelay   = TimeSpan.FromMilliseconds(300);
    private const           int      MissingSweepsToDrop = 2;
    // What VoiceControlGrain.SourceName renders TrackSource.Microphone as.
    private const string Microphone = "microphone";

    private IGrainTimer? _sweeper;

    private Guid                        ChannelId    => this.GetPrimaryKey();
    private Dictionary<Guid, RadioLink> Links        => activation.State.Links;
    private string                      RadioRoom    => new RadioRoomId(activation.State.SpaceId, ChannelId).ToRawRoomId();
    private IVoiceControlGrain          VoiceControl => grainFactory.GetGrain<IVoiceControlGrain>(Guid.Empty);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Timers do not travel: a migrated activation arrives with its links and arms the sweeper again.
        // A fresh one may find radio participants a lost activation left behind, so it sweeps at once.
        Arm(Links.Count > 0 ? SweepPeriod : TimeSpan.Zero);
        return Task.CompletedTask;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // Migration is not a shutdown: the links travel. Anything else ends the radio; the clients refetch.
        if (reason.ReasonCode != DeactivationReasonCode.Migrating)
            await RemoveAllAsync();
    }

    public async Task<IBroadcastLinksResult> IssueLinksAsync(Guid userId)
    {
        var (channel, error) = await CheckAsync(userId);
        if (channel is null)
            return new FailedBroadcastLinks(error);

        var now = DateTimeOffset.UtcNow;
        if (!Links.TryGetValue(userId, out var link))
            Links[userId] = link = new RadioLink { UserId = userId };

        if (link.Token.Length == 0 || now - link.IssuedAt >= TokenReuse)
        {
            link.Token    = await VoiceControl.IssueRadioTokenAsync(userId, channel.SpaceId, ChannelId);
            link.IssuedAt = now;
        }

        Arm(SweepPeriod);

        return new SuccessBroadcastLinks(await VoiceControl.GetRtcEndpointAsync(), link.Token,
            new RadioIdentity(userId).ToRawIdentity(), RadioRoom, ChannelBroadcast.ToDto(channel.Broadcast)!);
    }

    public async Task<IConfirmBroadcastLinksResult> ConfirmLinksAsync(Guid userId)
    {
        if (!Links.TryGetValue(userId, out var link))
            return new FailedConfirmBroadcastLinks(BroadcastLinksError.NOT_IN_CHANNEL);

        var (channel, error) = await CheckAsync(userId);
        if (channel is null)
            return new FailedConfirmBroadcastLinks(error);

        // The fork answers a forward that already exists with success, so a re-confirm after a
        // reconnect is one pass over the whole list.
        var targets   = await ValidTargetsAsync(channel);
        var forwarded = new HashSet<Guid>();
        foreach (var target in targets)
            if (await ForwardAsync(userId, target))
                forwarded.Add(target);

        // A pass that forwarded nothing says nothing about what the SFU still has.
        if (targets.Count > 0 && forwarded.Count == 0)
            return new FailedConfirmBroadcastLinks(BroadcastLinksError.SFU_UNAVAILABLE);

        link.ForwardedTargets.UnionWith(forwarded);
        link.Confirmed     = true;
        link.MissingSweeps = 0;
        return new SuccessConfirmBroadcastLinks(link.ForwardedTargets.Count);
    }

    public Task OnMemberJoinedAsync(Guid userId)
    {
        // Activating was the point; an idle radio takes one look for participants nobody was issued.
        logger.LogDebug("Radio of {Channel}: {User} joined", ChannelId, userId);
        Arm(TimeSpan.Zero);
        return Task.CompletedTask;
    }

    public async Task OnMemberLeftAsync(Guid userId)
    {
        if (!Links.Remove(userId))
            return;

        // The fork tears the forwards down with their source; the sweeper idles once the room is empty.
        await VoiceControl.RemoveParticipantAsync(RadioRoom, new RadioIdentity(userId).ToRawIdentity());
    }

    public Task ApplyVoiceRestrictionAsync(Guid userId, bool muted, bool deafened)
        => muted || deafened ? OnMemberLeftAsync(userId) : Task.CompletedTask;

    public async Task OnSettingsChangedAsync()
    {
        if (Links.Count == 0)
            return;

        var channel = await LoadChannelAsync();
        if (channel?.Broadcast is null)
        {
            await ShutdownAsync();
            return;
        }

        await ReconcileForwardsAsync(channel);
    }

    public async Task ShutdownAsync()
    {
        await RemoveAllAsync();
        Idle();
    }

    private async Task RemoveAllAsync()
    {
        foreach (var userId in Links.Keys.ToList())
            await VoiceControl.RemoveParticipantAsync(RadioRoom, new RadioIdentity(userId).ToRawIdentity());
        Links.Clear();
    }

    // ── checks ──────────────────────────────────────────────────────────────────────────────────

    private async Task<(ChannelEntity? Channel, BroadcastLinksError Error)> CheckAsync(Guid userId)
    {
        if (!callKit.Value.Sfu.Has(SfuInstanceCfg.ForwardCapability))
            return (null, BroadcastLinksError.SFU_UNAVAILABLE);

        // The slot before the mode: whether a channel broadcasts is not told to someone outside it.
        var channel = await LoadChannelAsync();
        if (channel is null || !await InChannelAsync(channel, userId))
            return (null, BroadcastLinksError.NOT_IN_CHANNEL);
        if (channel.Broadcast is null)
            return (null, BroadcastLinksError.NOT_A_BROADCAST_CHANNEL);

        var error = await CheckRightsAsync(channel, userId);
        return error == BroadcastLinksError.NONE ? (channel, error) : (null, error);
    }

    /// <summary>In this channel, allowed to Broadcast, neither server-muted nor deafened: what every link rests on.</summary>
    private async Task<BroadcastLinksError> CheckMemberAsync(ChannelEntity channel, Guid userId)
        => await InChannelAsync(channel, userId) ? await CheckRightsAsync(channel, userId) : BroadcastLinksError.NOT_IN_CHANNEL;

    private async Task<bool> InChannelAsync(ChannelEntity channel, Guid userId)
    {
        var slot = await grainFactory.GetGrain<ISpaceGrain>(channel.SpaceId).GetUserVoiceSlotAsync(userId);
        return slot?.ChannelId == ChannelId;
    }

    private async Task<BroadcastLinksError> CheckRightsAsync(ChannelEntity channel, Guid userId)
    {
        if (!await entitlementChecker.HasChannelAccessAsync(channel.SpaceId, ChannelId, userId, ArgonEntitlement.Broadcast))
            return BroadcastLinksError.INSUFFICIENT_PERMISSIONS;

        await using var ctx = await context.CreateDbContextAsync();
        var restricted = await ctx.UsersToServerRelations.AsNoTracking()
           .AnyAsync(m => m.SpaceId == channel.SpaceId && m.UserId == userId && !m.IsDeleted && (m.IsVoiceMuted || m.IsVoiceDeafened));
        return restricted ? BroadcastLinksError.SERVER_RESTRICTED : BroadcastLinksError.NONE;
    }

    private async Task<ChannelEntity?> LoadChannelAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();
        var channel = await ctx.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ChannelId && !c.IsDeleted);
        if (channel is not null)
            activation.State.SpaceId = channel.SpaceId;
        return channel;
    }

    /// <summary>Validated on save; this is for what changed since — a target deleted or turned into a broadcast channel.</summary>
    private async Task<List<Guid>> ValidTargetsAsync(ChannelEntity channel)
    {
        var targets = channel.Broadcast?.Targets ?? [];
        if (targets.Count == 0)
            return [];

        await using var ctx = await context.CreateDbContextAsync();
        return await ctx.Channels.AsNoTracking()
           .Where(c => targets.Contains(c.Id) && c.SpaceId == channel.SpaceId && !c.IsDeleted
                    && c.ChannelType == ChannelType.Voice && c.Broadcast == null)
           .Select(c => c.Id)
           .ToListAsync();
    }

    /// <summary>Every confirmed link forwarded into the targets that qualify now, and out of those that no longer do.</summary>
    private async Task ReconcileForwardsAsync(ChannelEntity channel)
    {
        var confirmed = Links.Values.Where(l => l.Confirmed).ToList();
        if (confirmed.Count == 0)
            return;

        var targets = (await ValidTargetsAsync(channel)).ToHashSet();
        foreach (var link in confirmed)
        {
            foreach (var added in targets.Except(link.ForwardedTargets).ToList())
                if (await ForwardAsync(link.UserId, added))
                    link.ForwardedTargets.Add(added);

            foreach (var dropped in link.ForwardedTargets.Except(targets).ToList())
            {
                await VoiceControl.RemoveParticipantAsync(new ArgonRoomId(channel.SpaceId, dropped).ToRawRoomId(),
                    new RadioIdentity(link.UserId).ToRawIdentity());
                link.ForwardedTargets.Remove(dropped);
            }
        }
    }

    private async Task<bool> ForwardAsync(Guid userId, Guid target)
    {
        var identity    = new RadioIdentity(userId).ToRawIdentity();
        var destination = new ArgonRoomId(activation.State.SpaceId, target).ToRawRoomId();
        if (await VoiceControl.ForwardParticipantAsync(RadioRoom, identity, destination))
            return true;

        await Task.Delay(ForwardRetryDelay);
        return await VoiceControl.ForwardParticipantAsync(RadioRoom, identity, destination);
    }

    // ── sweeper ─────────────────────────────────────────────────────────────────────────────────

    private void Arm(TimeSpan due)
        => _sweeper ??= this.RegisterGrainTimer(_ => SweepAsync(), new GrainTimerCreationOptions(due, SweepPeriod) { KeepAlive = true });

    private void Idle()
    {
        _sweeper?.Dispose();
        _sweeper = null;
    }

    /// <summary>
    /// Removes radio participants that no longer qualify, keeps the forwards in step with the targets
    /// and mutes a transmission past the cap. Idles once no link is out and the room answered empty.
    /// </summary>
    private async Task SweepAsync()
    {
        var channel = await LoadChannelAsync();
        if (channel?.Broadcast is null)
        {
            await ShutdownAsync();
            return;
        }

        var participants = await VoiceControl.ListParticipantsAsync(RadioRoom);
        var present      = new HashSet<Guid>();
        var now          = DateTimeOffset.UtcNow;

        foreach (var participant in participants)
        {
            if (!RadioIdentity.TryParse(participant.Identity, out var identity))
                continue;

            present.Add(identity.UserId);

            if (!Links.TryGetValue(identity.UserId, out var link)
             || await CheckMemberAsync(channel, identity.UserId) != BroadcastLinksError.NONE)
            {
                await VoiceControl.RemoveParticipantAsync(RadioRoom, participant.Identity);
                Links.Remove(identity.UserId);
                continue;
            }

            link.MissingSweeps = 0;
            await GuardTransmitAsync(channel.Broadcast, link, participant, now);
        }

        // A confirmed link whose participant is gone: the client left and the forwards went with it.
        // An empty answer is also what a failed call looks like, so it counts for nothing.
        if (participants.Count > 0)
            foreach (var link in Links.Values.Where(l => l.Confirmed && !present.Contains(l.UserId)).ToList())
                if (++link.MissingSweeps >= MissingSweepsToDrop)
                    Links.Remove(link.UserId);

        await ReconcileForwardsAsync(channel);

        logger.LogDebug("Radio {Room}: {Participants} participant(s), {Links} link(s)", RadioRoom, participants.Count, Links.Count);

        if (Links.Count == 0 && participants.Count == 0)
            Idle();
    }

    private async Task GuardTransmitAsync(ChannelBroadcast broadcast, RadioLink link, SfuParticipant participant, DateTimeOffset now)
    {
        var microphones = participant.Tracks.Where(t => t.Source == Microphone).ToList();
        foreach (var gone in link.UnmutedSince.Keys.Except(microphones.Select(t => t.Sid)).ToList())
            link.UnmutedSince.Remove(gone);

        foreach (var track in microphones)
        {
            if (track.Muted)
            {
                link.UnmutedSince.Remove(track.Sid);
                continue;
            }

            if (!link.UnmutedSince.TryGetValue(track.Sid, out var since))
            {
                link.UnmutedSince[track.Sid] = now;
                continue;
            }

            if (broadcast.MaxTransmitSeconds is { } cap && now - since > TimeSpan.FromSeconds(cap) + TransmitGrace)
            {
                await VoiceControl.MutePublishedTrackAsync(RadioRoom, participant.Identity, track.Sid, true);
                link.UnmutedSince.Remove(track.Sid);
            }
        }
    }
}

/// <summary>What a broadcast channel's radio holds; travels with a migration, see <see cref="VolatileGrainStorage"/>.</summary>
[GenerateSerializer]
public sealed record VoiceBroadcastActivationState
{
    [Id(0)]
    public Guid SpaceId { get; set; }

    [Id(1)]
    public Dictionary<Guid, RadioLink> Links { get; set; } = new();
}

/// <summary>One broadcaster's radio link: the token handed out, and what the SFU was told to forward.</summary>
[GenerateSerializer]
public sealed record RadioLink
{
    [Id(0)]
    public Guid UserId { get; set; }

    [Id(1)]
    public string Token { get; set; } = "";

    [Id(2)]
    public DateTimeOffset IssuedAt { get; set; }

    [Id(3)]
    public bool Confirmed { get; set; }

    [Id(4)]
    public HashSet<Guid> ForwardedTargets { get; set; } = [];

    /// <summary>Microphone track sid to when the sweeper first saw it unmuted.</summary>
    [Id(5)]
    public Dictionary<string, DateTimeOffset> UnmutedSince { get; set; } = new();

    [Id(6)]
    public int MissingSweeps { get; set; }
}
