namespace Argon.Users;

using ion.runtime;

public record RegistrationErrorConstants
{
    public RegistrationError Code    { get; set; }
    public string            Field   { get; set; }
    public string            Message { get; set; }

    public static FailedRegistration EmailAlreadyRegistered()
        => new(RegistrationError.EMAIL_ALREADY_REGISTERED, nameof(NewUserCredentialsInput.email), "Email already registered");

    public static FailedRegistration UsernameAlreadyTaken()
        => new(RegistrationError.USERNAME_ALREADY_TAKEN, nameof(NewUserCredentialsInput.username), "Username already taken");

    public static FailedRegistration UsernameReserved()
        => new(RegistrationError.USERNAME_RESERVED, nameof(NewUserCredentialsInput.username), "Username reserved");
    public static FailedRegistration InternalError()
        => new(RegistrationError.INTERNAL_ERROR, null, "Internal Error");
}