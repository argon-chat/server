namespace Argon.Users;

[MessagePackObject(true), TsInterface]
public record NewUserCredentialsInput(
    string Email,
    string Username,
    string? PhoneNumber,
    string Password,
    string DisplayName,
    DateTime BirthDate,
    bool AgreeTos,
    bool AgreeOptionalEmails,
    string? captchaToken);