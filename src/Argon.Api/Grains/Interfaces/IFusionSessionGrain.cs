namespace Argon.Grains.Interfaces;

using Features.Jwt;

[Alias("Argon.Grains.Interfaces.IFusionSessionGrain")]
public interface IFusionSessionGrain : IGrainWithGuidKey
{
    [Alias("BeginRealtimeSession")]
    ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null);

    [Alias("EndRealtimeSession")]
    ValueTask EndRealtimeSession();

    [Alias("HasSessionActive")]
    ValueTask<bool> HasSessionActive();

    [Alias("GetTokenUserData")]
    ValueTask<TokenUserData> GetTokenUserData();

    [Alias("SetActiveChannelConnection")]
    ValueTask SetActiveChannelConnection(Guid channelId);

    public const string StreamProviderId = "FusionSessionStream";
    public const string SelfNs = "@";
    public const string StorageId = "CacheStorage";
}
