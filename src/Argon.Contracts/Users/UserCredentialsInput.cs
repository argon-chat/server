namespace Argon.Users;

[MessagePackObject(true), TsInterface]
public sealed record UserCredentialsInput(
    string Email,
    string? Username,
    string? PhoneNumber,
    string? Password,
    string? OtpCode,
    string? captchaToken);