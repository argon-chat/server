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
    string? username,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    string password,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    string? machineKey,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    string email,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Key(4)]
    string? phoneNumber,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Key(5)]
    bool generateOtp = false);

public sealed record AuthorizeResponse(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    string token);