namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;
using Users;

[Alias($"Argon.Grains.Interfaces.{nameof(IUserSessionGrain)}")]
public interface IUserSessionGrain : IGrainWithGuidKey
{
    [Alias(nameof(BeginRealtimeSession))]
    ValueTask BeginRealtimeSession(Guid userId, Guid machineKey, UserStatus? preferredStatus = null);

    [Alias(nameof(EndRealtimeSession))]
    ValueTask EndRealtimeSession();

    [OneWay, Alias(nameof(HeartBeatAsync))]
    ValueTask HeartBeatAsync(UserStatus status);


    [OneWay, Alias(nameof(OnTypingEmit))]
    ValueTask OnTypingEmit(Guid serverId, Guid channelId);
    [OneWay, Alias(nameof(OnTypingStopEmit))]
    ValueTask OnTypingStopEmit(Guid serverId, Guid channelId);

    public const string StorageId = "CacheStorage";
}
