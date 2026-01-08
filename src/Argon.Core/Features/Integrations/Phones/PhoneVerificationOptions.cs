namespace Argon.Features.Integrations.Phones;

/// <summary>
/// Unified phone verification configuration.
/// Priority: Telegram (free, but only for Telegram users) -> Prelude/Twilio (paid fallback)
/// </summary>
public record PhoneVerificationOptions
{
    /// <summary>
    /// Enable phone verification. If false, NullChannel is used (logs codes, stores in memory).
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Telegram Gateway configuration.
    /// Attempted first as it's free, but only works for Telegram users.
    /// </summary>
    public TelegramChannelOptions Telegram { get; init; } = new();

    /// <summary>
    /// Prelude configuration. Used as fallback when Telegram fails.
    /// </summary>
    public PreludeChannelOptions Prelude { get; init; } = new();

    /// <summary>
    /// Twilio configuration. Used as fallback when Telegram fails and Prelude is disabled.
    /// </summary>
    public TwilioChannelOptions Twilio { get; init; } = new();
}

public record TelegramChannelOptions
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "https://gatewayapi.telegram.org";
    public string Token { get; init; } = string.Empty;
}

public record PreludeChannelOptions
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "https://api.prelude.dev";
    public string Token { get; init; } = string.Empty;
}

public record TwilioChannelOptions
{
    public bool Enabled { get; init; }
    public string AccountSid { get; init; } = string.Empty;
    public string AuthToken { get; init; } = string.Empty;
    public string FromNumber { get; init; } = string.Empty;
    public string VerifyServiceSid { get; init; } = string.Empty;
}
