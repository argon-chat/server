namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;

public interface IUserAuthorization : IRpcService
{
    Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request);
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record AuthorizeRequest(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    string? username,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1)]
    string password,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2)]
    string? machineKey,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Key(x: 3)]
    string email,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Key(x: 4)]
    string? phoneNumber,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Key(x: 5)]
    bool generateOtp = false);

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record AuthorizeResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0)]
    string token);