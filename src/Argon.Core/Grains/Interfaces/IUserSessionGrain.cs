namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;
using Users;

[Alias($"Argon.Grains.Interfaces.{nameof(IUserSessionGrain)}")]
public interface IUserSessionGrain : IGrainWithGuidKey
{
    [Alias(nameof(BeginRealtimeSession))]
    ValueTask BeginRealtimeSession(UserStatus? preferredStatus = null);

    [Alias(nameof(EndRealtimeSession))]
    ValueTask EndRealtimeSession();

    [Alias(nameof(HeartBeatAsync))]
    ValueTask<bool> HeartBeatAsync(UserStatus status);


    [OneWay, Alias(nameof(OnTypingEmit))]
    ValueTask OnTypingEmit(Guid channelId);
    [OneWay, Alias(nameof(OnTypingStopEmit))]
    ValueTask OnTypingStopEmit(Guid channelId);

    public const string StorageId = "CacheStorage";
}

public class ArgonDropConnectionException(string msg) : InvalidOperationException(msg);
