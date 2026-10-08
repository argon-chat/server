namespace Argon.Features.Integrations.Phones;

/// <summary>
/// Represents a single phone verification channel (Telegram, Prelude, Twilio, etc.)
/// </summary>
public interface IPhoneChannel
{
    /// <summary>
    /// Channel identifier for logging and debugging.
    /// </summary>
    PhoneChannelKind Kind { get; }

    /// <summary>
    /// Whether this channel is enabled in configuration.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Check if this channel can send to the given phone number.
    /// For Telegram: checks if user is registered.
    /// For others: typically returns true if enabled.
    /// </summary>
    Task<bool> CanSendAsync(string phoneNumber, CancellationToken ct = default);

    /// <summary>
    /// Send verification code to the phone number.
    /// </summary>
    /// <returns>Request ID for verification, or null if failed.</returns>
    Task<PhoneSendResult> SendCodeAsync(PhoneSendRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verify the code entered by user.
    /// </summary>
    Task<PhoneVerifyResult> VerifyCodeAsync(PhoneVerifyRequest request, CancellationToken ct = default);
}

public record PhoneSendRequest(
    string PhoneNumber,
    string UserIp,
    string UserAgent,
    string AppVersion,
    int CodeLength = 6);

public record PhoneSendResult(
    bool Success,
    string? RequestId = null,
    PhoneChannelKind? UsedChannel = null,
    string? ErrorReason = null);

public record PhoneVerifyRequest(
    string PhoneNumber,
    string? RequestId,
    string Code);

public record PhoneVerifyResult(
    PhoneVerifyStatus Status,
    int RemainingAttempts = 0,
    DateTime? RetryAfter = null);

public enum PhoneVerifyStatus
{
    Verified,
    InvalidCode,
    Expired,
    TooManyAttempts,
    NotFound,
    Error
}
