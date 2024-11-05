namespace Argon.Api.Grains.Interfaces;

using System.Runtime.Serialization;
using Entities;
using MemoryPack;
using MessagePack;

public interface IUserManager : IGrainWithGuidKey
{
    [Alias(alias: "CreateUser")]
    Task<UserDto> CreateUser(UserCredentialsInput input);

    [Alias(alias: "UpdateUser")]
    Task<UserDto> UpdateUser(UserCredentialsInput input);

    [Alias(alias: "DeleteUser")]
    Task DeleteUser();

    [Alias(alias: "GetUser")]
    Task<UserDto> GetUser();
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(alias: nameof(UserCredentialsInput))]
public sealed partial record UserCredentialsInput(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0), Id(id: 0)]
    string Email,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1), Id(id: 1)]
    string? Username,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2), Id(id: 2)]
    string? PhoneNumber,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Key(x: 3), Id(id: 3)]
    string? Password,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Key(x: 4), Id(id: 4)]
    string? PasswordConfirmation,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Key(x: 5), Id(id: 5)]
    bool GenerateOtp
);