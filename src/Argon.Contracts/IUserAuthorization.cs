namespace Argon.Contracts;

using MessagePack;
using Reinforced.Typings.Attributes;

[MessagePackObject(true), TsInterface]
public sealed partial record UserCredentialsInput(
    string Email,
    string? Username,
    string? PhoneNumber,
    string? Password,
    string? OtpCode);

[MessagePackObject(true), TsInterface]
public sealed partial record UserEditInput(
    string? Username,
    string? DisplayName,
    string? AvatarId);

[MessagePackObject(true), TsInterface]
public record NewUserCredentialsInput(
    string Email,
    string Username,
    string? PhoneNumber,
    string Password,
    string DisplayName,
    DateTime BirthDate,
    bool AgreeTos,
    bool AgreeOptionalEmails);


[MessagePackObject(true), TsInterface]
public sealed partial record UserConnectionInfo(
    string Region,
    string IpAddress,
    string ClientName,
    string HostName);