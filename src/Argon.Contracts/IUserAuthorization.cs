namespace Argon.Contracts;

using MemoryPack;
using Orleans;

[MemoryPackable, Serializable, GenerateSerializer, Alias(nameof(UserCredentialsInput))]
public sealed partial record UserCredentialsInput(
    [field: Id(0)] string Email,
    [field: Id(1)] string? Username,
    [field: Id(2)] string? PhoneNumber,
    [field: Id(3)] string? Password,
    [field: Id(4)] string? OtpCode);

[MemoryPackable, Serializable, GenerateSerializer, Alias(nameof(NewUserCredentialsInput))]
public sealed partial record NewUserCredentialsInput(
    [field: Id(0)] string Email,
    [field: Id(1)] string Username,
    [field: Id(2)] string? PhoneNumber,
    [field: Id(3)] string Password,
    [field: Id(4)] string DisplayName,
    [field: Id(5)] DateTime BirthDate,
    [field: Id(6)] bool AgreeTos,
    [field: Id(7)] bool AgreeOptionalEmails);

[MemoryPackable, Serializable, GenerateSerializer, Alias(nameof(UserConnectionInfo))]
public sealed partial record UserConnectionInfo(
    [field: Id(0)] string Region,
    [field: Id(1)] string IpAddress,
    [field: Id(2)] string ClientName,
    [field: Id(3)] string HostName);