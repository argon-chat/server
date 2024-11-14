namespace Argon.Contracts;

using Orleans;

[Alias("Argon.AuthorizationError"), GenerateSerializer]
public enum AuthorizationError
{
    BAD_CREDENTIALS,
    REQUIRED_OTP,
    BAD_OTP
}

[Alias("Argon.RegistrationError"), GenerateSerializer]
public enum RegistrationError
{
    USERNAME_ALREADY_TAKEN,
    USERNAME_RESERVED,
    EMAIL_ALREADY_REGISTERED,
    REGION_BANNED,
    EMAIL_BANNED,
    SSO_EMAILS_NOT_ALLOWED,
    INTERNAL_ERROR
}