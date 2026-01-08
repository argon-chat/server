namespace Argon.Features.Integrations.Phones;

public enum VerifyStatus
{
    Verified,
    WrongCode,
    TooManyAttempts
}

public record VerifyResult(
    VerifyStatus verifyResult,
    int attemptCount,
    DateTime? RetryAfterUtc = null);
