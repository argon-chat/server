namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;

public interface IUserAuthorization : IRpcService
{
    Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request);
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record AuthorizeRequest(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    string username,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    string password,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    string machineKey);

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
public sealed partial record AuthorizeResponse(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    string token);