namespace Argon.Api.Grains.Interfaces;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public interface IUserManager : IGrainWithGuidKey
{
    [Alias("CreateUser")]
    Task CreateUser(UserCredentialsInput input);

    [Alias("UpdateUser")]
    Task UpdateUser(UserCredentialsInput input);

    [Alias("DeleteUser")]
    Task DeleteUser();

    [Alias("GetUser")]
    Task GetUser();
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(UserCredentialsInput))]
public sealed partial record UserCredentialsInput(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    [property: Id(0)]
    string Email,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    [property: Id(1)]
    string? Username,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    [property: Id(2)]
    string? PhoneNumber,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    [property: Id(3)]
    string? Password,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Key(4)]
    [property: Id(4)]
    string? PasswordConfirmation,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Key(5)]
    [property: Id(5)]
    bool GenerateOtp
);