namespace Argon.Api.Grains.Interfaces;

[Alias("Argon.Api.Grains.Interfaces.IFusionSessionGrain")]
public interface IFusionSessionGrain : IGrainWithGuidCompoundKey
{
    [Alias("BeginRealtimeSession")]
    ValueTask BeginRealtimeSession(Guid userId, Guid machineKey);

    [Alias("EndRealtimeSession")]
    ValueTask EndRealtimeSession();

    [Alias("HasSessionActive")]
    ValueTask<bool> HasSessionActive();

    [Alias("Signal")]
    ValueTask Signal();


    public const string StreamProviderId = "FusionSessionStream";
    public const string SelfNs = "@";
}
