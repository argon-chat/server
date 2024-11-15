namespace Argon.Api.Grains.Interfaces;

using Features.Jwt;

[Alias("Argon.Api.Grains.Interfaces.IFusionSessionGrain")]
public interface IFusionSessionGrain : IGrainWithGuidKey
{
    [Alias("BeginRealtimeSession")]
    ValueTask BeginRealtimeSession(Guid userId, Guid machineKey);

    [Alias("EndRealtimeSession")]
    ValueTask EndRealtimeSession();

    [Alias("HasSessionActive")]
    ValueTask<bool> HasSessionActive();

    [Alias("Signal")]
    ValueTask Signal();

    [Alias("GetTokenUserData")]
    ValueTask<TokenUserData> GetTokenUserData();

    public const string StreamProviderId = "FusionSessionStream";
    public const string SelfNs = "@";
    public const string StorageId = "CacheStorage";
}
