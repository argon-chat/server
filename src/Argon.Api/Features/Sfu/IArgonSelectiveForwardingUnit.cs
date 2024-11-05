namespace Argon.Sfu;

public interface IArgonSelectiveForwardingUnit
{
    /// <summary>
    ///     Issue a realtime authorization token for use RealtimeCall
    /// </summary>
    /// <param name="userId">
    ///     Argon User
    /// </param>
    /// <param name="channelId">
    ///     Argon channel Id (composite server & channel key)
    /// </param>
    /// <param name="permission">
    ///     defined permissions for user
    /// </param>
    /// <returns>
    ///     Realtime token for connect to channel
    /// </returns>
    ValueTask<RealtimeToken> IssueAuthorizationTokenAsync(ArgonUserId userId, ArgonChannelId channelId,
        SfuPermission permission);

    /// <summary>
    ///     Set mute or unmute for participant
    /// </summary>
    ValueTask<bool> SetMuteParticipantAsync(bool isMuted, ArgonUserId userId, ArgonChannelId channelId);

    /// <summary>
    ///     Kick participant from channel
    /// </summary>
    ValueTask<bool> KickParticipantAsync(ArgonUserId userId, ArgonChannelId channelId);

    /// <summary>
    ///     Get or Create ephemeral channel
    /// </summary>
    ValueTask<EphemeralChannelInfo> EnsureEphemeralChannelAsync(ArgonChannelId channelId, uint maxParticipants);

    /// <summary>
    ///     dispose ephemeral channel
    /// </summary>
    ValueTask<bool> PruneEphemeralChannelAsync(ArgonChannelId channelId);
}