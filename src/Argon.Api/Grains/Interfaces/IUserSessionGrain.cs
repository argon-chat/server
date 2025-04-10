namespace Argon.Grains.Interfaces;

[Alias($"Argon.Grains.Interfaces.{nameof(IUserSessionGrain)}")]
public interface IUserSessionGrain : IGrainWithGuidKey
{
    [Alias(nameof(BeginRealtimeSession))]
    ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null);

    [Alias(nameof(EndRealtimeSession))]
    ValueTask EndRealtimeSession();

    public const string StorageId = "CacheStorage";
}
