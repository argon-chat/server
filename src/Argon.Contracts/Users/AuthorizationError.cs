namespace Argon.Users;

[TsEnum]
public enum AuthorizationError
{
    BAD_CREDENTIALS,
    REQUIRED_OTP,
    BAD_OTP
}