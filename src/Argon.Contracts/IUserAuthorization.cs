namespace Argon.Contracts;

using MemoryPack;
using Orleans;

[MemoryPackable, Serializable, GenerateSerializer, Alias(nameof(UserCredentialsInput))]
public sealed partial record UserCredentialsInput(
    string Email,
    string? Username,
    string? PhoneNumber,
    string? Password,
    string? OtpCode);


[MemoryPackable, Serializable, GenerateSerializer, Alias(nameof(UserCredentialsInput))]
public sealed partial record UserConnectionInfo(
    string Region,
    string IpAddress,
    string ClientName,
    string HostName);