namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;

public interface IUserInteraction : IRpcService
{
    Task<UserResponse>         GetMe();
    Task<ServerResponse>       CreateServer(CreateServerRequest request);
    Task<List<ServerResponse>> GetServers();

    Task<List<ServerDetailsResponse>> GetServerDetails(ServerDetailsRequest request);

    // Task CreateChannel(string username);
    Task<ChannelJoinResponse> JoinChannel(ChannelJoinRequest request);
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record ServerDetailsRequest(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    Guid ServerId
);

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record UserResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1)]
    string Username,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2)]
    string AvatarUrl,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Key(x: 3)]
    DateTime CreatedAt,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Key(x: 4)]
    DateTime UpdatedAt
);

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record CreateServerRequest(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1)]
    string Description,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2)]
    string AvatarUrl
);

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record ServerResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1)]
    string Name,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2)]
    string Description,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Key(x: 3)]
    string AvatarUrl,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Key(x: 4)]
    List<ServerDetailsResponse> Channels,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Key(x: 5)]
    DateTime CreatedAt,
    [property: DataMember(Order = 6), MemoryPackOrder(order: 6), Key(x: 6)]
    DateTime UpdatedAt
    // TODO: all users of the server with their statuses(online/offline/in channel)
);

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record ServerDetailsResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1)]
    string Name,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2)]
    string Description,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Key(x: 3)]
    string CreatedBy,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Key(x: 4)]
    string ChannelType,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Key(x: 5)]
    string AccessLevel,
    [property: DataMember(Order = 6), MemoryPackOrder(order: 6), Key(x: 6)]
    DateTime CreatedAt,
    [property: DataMember(Order = 7), MemoryPackOrder(order: 7), Key(x: 7)]
    DateTime UpdatedAt
    // TODO: all users currently connected to this channel
);

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record ChannelJoinRequest(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    Guid ServerId,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1)]
    Guid ChannelId
);

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record ChannelJoinResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    string Token
);