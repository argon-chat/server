namespace Argon.Core.Features.CoreLogic.Privacy;

using Argon.Core.Entities.Data;

/// <summary>
/// Registry of privacy-rule keys (the "about-me" policy axis). Add a new constant to
/// extend the flexible privacy system to a new behaviour — no schema change is needed.
/// Each key also declares its default disposition when a user has no explicit rule.
/// </summary>
public static class PrivacyKeys
{
    /// <summary>Who may draw on this user's shared screen (screencast drawing).</summary>
    public const string StreamDraw = "stream.draw";

    /// <summary>All known keys (for validation of incoming rule writes).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        StreamDraw,
    };

    /// <summary>Default disposition when a user has set no rule for a key.</summary>
    public static PrivacyMode DefaultModeFor(string key) => key switch
    {
        StreamDraw => PrivacyMode.Everybody,
        _          => PrivacyMode.Everybody,
    };
}
