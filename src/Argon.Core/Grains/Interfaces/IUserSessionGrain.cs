namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;
using Users;

// Keyed by "{userId}:{sid}" — one grain per stable per-launch session id (sid), NOT per transport
// connection. A reconnect of the same client re-attaches to the same grain instead of churning a new
// one, which is what stops multi-device presence from flapping. The userId is encoded in the key so
// ReceiveReminder (which runs without a request context) can still resolve it.
[Alias($"Argon.Grains.Interfaces.{nameof(IUserSessionGrain)}")]
public interface IUserSessionGrain : IGrainWithStringKey
{
    // A transport connection (SignalR ConnectionId) attached to this session. The first attach starts
    // the session; further attaches (reconnect / second window) just join the live-connection set.
    [Alias(nameof(AttachConnectionAsync))]
    ValueTask AttachConnectionAsync(string connectionId, UserStatus? preferredStatus = null);

    // A transport connection dropped. When the last one goes, the session does NOT go offline
    // immediately — it arms a durable grace reminder and lets the presence TTL ride out transient
    // drops (OS sleep/modern-standby reconnects within the window). Reliable cleanup, no flap.
    [Alias(nameof(DetachConnectionAsync))]
    ValueTask DetachConnectionAsync(string connectionId);

    // Explicit, intentional offline (logout / quit / account switch): take this session offline now,
    // bypassing the grace window. Network drops use the grace; deliberate exits are immediate.
    [Alias(nameof(GoOfflineAsync))]
    ValueTask GoOfflineAsync();

    [Alias(nameof(HeartBeatAsync))]
    ValueTask<bool> HeartBeatAsync(string connectionId, UserStatus status);


    [OneWay, Alias(nameof(OnTypingEmit))]
    ValueTask OnTypingEmit(Guid channelId);
    [OneWay, Alias(nameof(OnTypingStopEmit))]
    ValueTask OnTypingStopEmit(Guid channelId);

    public const string StorageId = "CacheStorage";
}

public class ArgonDropConnectionException(string msg) : InvalidOperationException(msg);
