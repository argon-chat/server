namespace Argon.Grains.Interfaces;

using Sfu;
using Argon.Servers;

[Alias("Argon.Grains.Interfaces.IChannelGrain")]
public interface IChannelGrain : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<Maybe<RealtimeToken>> Join(Guid userId);

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("GetChannel")]
    Task<Channel> GetChannel();

    [Alias("UpdateChannel")]
    Task<Channel> UpdateChannel(ChannelInput input);


    // for join\leave\mute\unmute notifications
    public const string UserTransformNotificationStream  = $"{nameof(IChannelGrain)}.user.transform";
    public const string ChannelMessageNotificationStream = $"{nameof(IChannelGrain)}.user.messages";
}

[MessagePackObject(true)]
public sealed record ChannelInput(
    string Name,
    ChannelEntitlementOverwrite AccessLevel,
    string? Description,
    ChannelType ChannelType);