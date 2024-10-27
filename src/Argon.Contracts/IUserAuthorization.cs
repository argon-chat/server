namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Fusion;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;

public interface IUserAuthorization : IRpcService
{
    Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request);
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record AuthorizeRequest(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0)] string username,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1)] string password,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2)] string machineKey);


public sealed partial record ServerResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0)] Guid serverId,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1)] string serverName,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2)] string? avatarFileId
);

public sealed partial record AuthorizeResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0)] string token,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1)] List<ServerResponse> servers);