namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;

public interface IUserInteraction : IRpcService
{
    Task<UserResponse> GetMe();
    Task<ServerResponse> CreateServer(CreateServerRequest request);
    Task<List<ServerResponse>> GetServers();

    Task<List<ServerDetailsResponse>> GetServerDetails();

    // Task CreateChannel(string username);
    Task<ChannelJoinResponse> JoinChannel(ChannelJoinRequest request);
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record UserResponse(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    Guid Id,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    string Username,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    string AvatarUrl,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    DateTime CreatedAt,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Key(4)]
    DateTime UpdatedAt
);

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record CreateServerRequest(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    string Name,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    string Description,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    string AvatarUrl
);

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record ServerResponse(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    Guid Id,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    string Name,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    string Description,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    string AvatarUrl,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Key(4)]
    List<Guid> Channels,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Key(5)]
    DateTime CreatedAt,
    [property: DataMember(Order = 6)]
    [property: MemoryPackOrder(6)]
    [property: Key(6)]
    DateTime UpdatedAt
    // TODO: all users of the server with their statuses(online/offline/in channel)
);

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record ServerDetailsResponse(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    Guid Id,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    string Name,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    string Description,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    string CreatedBy,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Key(4)]
    string ChannelType,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Key(5)]
    string AccessLevel,
    [property: DataMember(Order = 6)]
    [property: MemoryPackOrder(6)]
    [property: Key(6)]
    DateTime CreatedAt,
    [property: DataMember(Order = 7)]
    [property: MemoryPackOrder(7)]
    [property: Key(7)]
    DateTime UpdatedAt
    // TODO: all users currently connected to this channel
);

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record ChannelJoinRequest(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    Guid ServerId,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    Guid ChannelId
);

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record ChannelJoinResponse(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    string Token
);