namespace Argon.Users;

public enum RegistrationError
{
    USERNAME_ALREADY_TAKEN,
    USERNAME_RESERVED,
    EMAIL_ALREADY_REGISTERED,
    REGION_BANNED,
    EMAIL_BANNED,
    SSO_EMAILS_NOT_ALLOWED,
    INTERNAL_ERROR,
    VALIDATION_FAILED
}

public record RegistrationErrorData
{
    public RegistrationError Code    { get; set; }
    public string            Field   { get; set; }
    public string            Message { get; set; }


    public static RegistrationErrorData EmailAlreadyRegistered() => new()
    {
        Code    = RegistrationError.EMAIL_ALREADY_REGISTERED,
        Message = "Email already registered",
        Field   = nameof(NewUserCredentialsInput.Email)
    };

    public static RegistrationErrorData UsernameAlreadyTaken() => new()
    {
        Code    = RegistrationError.USERNAME_ALREADY_TAKEN,
        Message = "Username already taken",
        Field   = nameof(NewUserCredentialsInput.Username)
    };

    public static RegistrationErrorData UsernameReserved() => new()
    {
        Code    = RegistrationError.USERNAME_RESERVED,
        Message = "Username reserved",
        Field   = nameof(NewUserCredentialsInput.Username)
    };

    public static RegistrationErrorData InternalError() => new()
    {
        Code    = RegistrationError.INTERNAL_ERROR,
        Message = "Internal Error",
        Field   = nameof(NewUserCredentialsInput.Username)
    };
}