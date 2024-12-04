namespace Argon.Users;

public enum AuthorizationError
{
    NONE,
    BAD_CREDENTIALS,
    REQUIRED_OTP,
    BAD_OTP
}