namespace Argon.Contracts;

using System.Runtime.Serialization;
using ActualLab.Rpc;
using MemoryPack;
using MessagePack;

public interface IUserAuthorization : IRpcService
{
    Task<AuthorizeResponse> AuthorizeAsync(AuthorizeRequest request);
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record AuthorizeRequest(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0)]
    string? username,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1)]
    string password,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2)]
    string? machineKey,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Key(3)]
    string email,
    [property: DataMember(Order = 4), MemoryPackOrder(4), Key(4)]
    string? phoneNumber,
    [property: DataMember(Order = 5), MemoryPackOrder(5), Key(5)]
    bool generateOtp = false
);

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public sealed partial record AuthorizeResponse(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0)]
    string token
);
