using Argon.Api.Features.CoreLogic.Otp;

namespace Argon.Api.Features.CoreLogic.Otp;

public enum OtpMethod
{
    Email,
    Phone,
    Totp
}

public enum OtpPurpose { SignIn, ChangeEmail, ResetPassword }
public record SendOtpRequest(string Target, Guid UserId, OtpPurpose Purpose, string? DeviceId, OtpMethod Method);
public record VerifyOtpRequest(string Target, Guid UserId, OtpPurpose Purpose, string Code, string? DeviceId, OtpMethod Method);

public sealed record OtpRecord(
    string HashBase64,
    string SaltBase64,
    DateTimeOffset Expiry,
    int AttemptsLeft,
    string? RequestId,
    string? DeviceId
);