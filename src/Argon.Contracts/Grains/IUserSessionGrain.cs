namespace Argon.Grains.Interfaces;

using Users;

[Alias($"Argon.Grains.Interfaces.{nameof(IUserSessionGrain)}")]
public interface IUserSessionGrain : IGrainWithGuidKey
{
    [Alias(nameof(BeginRealtimeSession))]
    ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null);

    [Alias(nameof(EndRealtimeSession))]
    ValueTask EndRealtimeSession();

    [Alias(nameof(HeartBeatAsync))]
    ValueTask HeartBeatAsync(UserStatus status);

    public const string StorageId = "CacheStorage";
}
