namespace Argon.Grains.Interfaces;

/// <summary>
/// The radio of one broadcast channel, keyed by that channel's id. Volatile: the space's voice slot,
/// the entitlements and the channel settings are the authority; this only mints links and keeps
/// the SFU forwarding in step with them.
/// </summary>
[Alias("Argon.Grains.Interfaces.IVoiceBroadcastGrain")]
public interface IVoiceBroadcastGrain : IGrainWithGuidKey
{
    /// <summary>The radio token for a member of this channel: in it, allowed to Broadcast, not server-restricted.</summary>
    [Alias(nameof(IssueLinksAsync))]
    Task<IBroadcastLinksResult> IssueLinksAsync(Guid userId);

    /// <summary>The radio participant is connected: forward it into the current targets.</summary>
    [Alias(nameof(ConfirmLinksAsync))]
    Task<IConfirmBroadcastLinksResult> ConfirmLinksAsync(Guid userId);

    /// <summary>Left, kicked or moved out: the radio participant goes with them.</summary>
    [Alias(nameof(OnMemberLeftAsync))]
    Task OnMemberLeftAsync(Guid userId);

    /// <summary>A server mute or deafen: a restricted member does not transmit.</summary>
    [Alias(nameof(ApplyVoiceRestrictionAsync))]
    Task ApplyVoiceRestrictionAsync(Guid userId, bool muted, bool deafened);

    /// <summary>Mode or targets changed: recompute the forwarding, revoke what dropped out.</summary>
    [Alias(nameof(OnSettingsChangedAsync))]
    Task OnSettingsChangedAsync();

    /// <summary>The channel is gone or its mode is off: revoke every radio participant.</summary>
    [Alias(nameof(ShutdownAsync))]
    Task ShutdownAsync();
}
