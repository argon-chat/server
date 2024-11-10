namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Collections;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;

public interface IUserInteraction : IRpcService
{
    Task<UserResponse>         GetMe();
    Task<ServerResponse>       CreateServer(CreateServerRequest request);
    Task<List<ServerResponse>> GetServers();
}

public interface IServerInteraction : IRpcService
{
    ValueTask<Guid>                   CreateChannel(Guid serverId, string name, ChannelType kind);
    ValueTask                         DeleteChannel(Guid serverId, Guid channelId);
    ValueTask<ChannelJoinResponse>    JoinToVoiceChannel(Guid serverId, Guid channelId);
}

public interface IEventBus : IRpcService
{
    ValueTask<RpcStream<IArgonEvent>> SubscribeToServerEvents<T>(Guid ServerId) where T : ArgonEvent<T>; 
}


public enum ChannelType : ushort
{
    Text,
    Voice,
    Announcement
}

public enum ServerEventKind
{
}

public interface IRawEvent;

public interface IArgonEvent : IRawEvent
{
    public abstract static string ProviderId { get; }
    public abstract static string Namespace  { get; }
}

public record ArgonEvent<T> : IArgonEvent where T : ArgonEvent<T>, IArgonEvent
{
    public static string Namespace => $".{typeof(T).FullName}";
    public string EventId    => T.ProviderId + T.Namespace;
    public static string ProviderId  => "argon.cluster.events";
}


[MemoryPackable] public partial record JoinToServerUser(Guid userId) : ArgonEvent<JoinToServerUser>;
[MemoryPackable] public partial record LeaveFromServerUser(Guid userId) : ArgonEvent<LeaveFromServerUser>;
[MemoryPackable] public partial record JoinedToChannelUser(Guid userId, Guid channelId) : ArgonEvent<JoinedToChannelUser>;
[MemoryPackable] public partial record LeavedFromChannelUser(Guid userId, Guid channelId) : ArgonEvent<LeavedFromChannelUser>;
[MemoryPackable] public partial record ChannelCreated(Guid channelId) : ArgonEvent<ChannelCreated>;
[MemoryPackable] public partial record ChannelModified(Guid channelId, PropertyBag bag) : ArgonEvent<ChannelModified>;
[MemoryPackable] public partial record ChannelRemoved(Guid channelId) : ArgonEvent<ChannelRemoved>;
[MemoryPackable] public partial record UserChangedStatus(Guid userId, UserStatus status, PropertyBag bag) : ArgonEvent<UserChangedStatus>;
 

public enum UserStatus
{
    Offline,
    Online,
    Away,
    InGame,
    DoNotDisturb
}

[MemoryPackable]
public sealed partial record ServerDetailsRequest(Guid ServerId);

[MemoryPackable]
public sealed partial record UserResponse(
    Guid Id,
    string Username,
    string AvatarUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt);

[MemoryPackable]
public sealed partial record CreateServerRequest(
    string Name,
    string Description,
    string AvatarUrl);

[MemoryPackable]
public sealed partial record ServerResponse(
    Guid Id,
    string Name,
    string Description,
    string AvatarUrl,
    List<ServerDetailsResponse> Channels,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

[MemoryPackable]
public sealed partial record ServerDetailsResponse(
    Guid Id,
    string Name,
    string Description,
    string CreatedBy,
    string ChannelType,
    string AccessLevel,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

[MemoryPackable]
public sealed partial record ChannelJoinRequest(
    Guid ServerId,
    Guid ChannelId);

[MemoryPackable]
public sealed partial record ChannelJoinResponse(
    string Token);