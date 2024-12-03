namespace Argon.Users;

[MessagePackObject(true), TsInterface]
public sealed record UserEditInput(
    string? Username,
    string? DisplayName,
    string? AvatarId);