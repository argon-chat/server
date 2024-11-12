namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Collections;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;
using Orleans;

public interface IUserInteraction : IRpcService
{
    Task<UserResponse>           GetMe();
    Task<ServerDefinition>       CreateServer(CreateServerRequest request);
    Task<List<ServerDefinition>> GetServers();
}

public interface IServerInteraction : IRpcService
{
    ValueTask<Guid>                CreateChannel(Guid serverId, string name, ChannelType kind);
    ValueTask                      DeleteChannel(Guid serverId, Guid channelId);
    ValueTask<ChannelJoinResponse> JoinToVoiceChannel(Guid serverId, Guid channelId);
}

public interface IEventBus : IRpcService
{
    ValueTask<RpcStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId);
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

public interface IArgonEvent
{
    public static string ProviderId => "argon.cluster.events";
    public static string Namespace  => $"@";
}

[MemoryPackable, GenerateSerializer]
public partial record ArgonEvent<T> : IArgonEvent where T : ArgonEvent<T>, IArgonEvent;

[MemoryPackable, GenerateSerializer]
public partial record JoinToServerUser([property: Id(0)] Guid userId) : ArgonEvent<JoinToServerUser>;

[MemoryPackable, GenerateSerializer]
public partial record LeaveFromServerUser([property: Id(0)] Guid userId) : ArgonEvent<LeaveFromServerUser>;

[MemoryPackable, GenerateSerializer]
public partial record JoinedToChannelUser([property: Id(0)] Guid userId, [property: Id(1)] Guid channelId) : ArgonEvent<JoinedToChannelUser>;

[MemoryPackable, GenerateSerializer]
public partial record LeavedFromChannelUser([property: Id(0)] Guid userId, [property: Id(1)] Guid channelId) : ArgonEvent<LeavedFromChannelUser>;

[MemoryPackable, GenerateSerializer]
public partial record ChannelCreated([property: Id(0)] Guid channelId) : ArgonEvent<ChannelCreated>;

[MemoryPackable, GenerateSerializer]
public partial record ChannelModified([property: Id(0)] Guid channelId, [property: Id(1)] PropertyBag bag) : ArgonEvent<ChannelModified>;

[MemoryPackable, GenerateSerializer]
public partial record ChannelRemoved([property: Id(0)] Guid channelId) : ArgonEvent<ChannelRemoved>;

[MemoryPackable, GenerateSerializer]
public partial record UserChangedStatus([property: Id(0)] Guid userId, [property: Id(1)] UserStatus status, [property: Id(3)] PropertyBag bag)
    : ArgonEvent<UserChangedStatus>;

[MemoryPackable, GenerateSerializer]
public partial record ServerModified([property: Id(0)] PropertyBag bag) : ArgonEvent<ServerModified>;

public enum UserStatus
{
    Offline,
    Online,
    Away,
    InGame,
    Listen,
    TouchGrass,
    DoNotDisturb
}

[MemoryPackable]
public sealed partial record ServerDetailsRequest(Guid ServerId);

[MemoryPackable]
public sealed partial record ServerUser(
    UserResponse user,
    string Role);

[MemoryPackable]
public sealed partial record UserResponse(
    Guid Id,
    string Username,
    string AvatarUrl,
    string DisplayName,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public Guid     Id          { get; set; } = Id;
    public string   Username    { get; set; } = Username;
    public string   AvatarUrl   { get; set; } = AvatarUrl;
    public string   DisplayName { get; set; } = DisplayName;
    public DateTime CreatedAt   { get; set; } = CreatedAt;
    public DateTime UpdatedAt   { get; set; } = UpdatedAt;
}

[MemoryPackable]
public sealed partial record CreateServerRequest(
    string Name,
    string Description,
    string AvatarUrl);

[MemoryPackable]
public sealed partial record ServerDefinition(
    Guid Id,
    string Name,
    string Description,
    string AvatarUrl,
    List<ChannelDefinition> Channels,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public Guid                    Id          { get; set; } = Id;
    public string                  Name        { get; set; } = Name;
    public string                  Description { get; set; } = Description;
    public string                  AvatarUrl   { get; set; } = AvatarUrl;
    public List<ChannelDefinition> Channels    { get; set; } = Channels;
    public DateTime                CreatedAt   { get; set; } = CreatedAt;
    public DateTime                UpdatedAt   { get; set; } = UpdatedAt;
}

[MemoryPackable]
public sealed partial record ChannelDefinition(
    Guid Id,
    string Name,
    string Description,
    string CreatedBy,
    string ChannelType,
    string AccessLevel,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public Guid     Id          { get; set; } = Id;
    public string   Name        { get; set; } = Name;
    public string   Description { get; set; } = Description;
    public string   CreatedBy   { get; set; } = CreatedBy;
    public string   ChannelType { get; set; } = ChannelType;
    public string   AccessLevel { get; set; } = AccessLevel;
    public DateTime CreatedAt   { get; set; } = CreatedAt;
    public DateTime UpdatedAt   { get; set; } = UpdatedAt;
}

[MemoryPackable]
public sealed partial record ChannelJoinRequest(
    Guid ServerId,
    Guid ChannelId);

[MemoryPackable]
public sealed partial record ChannelJoinResponse(
    string Token);