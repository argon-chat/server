namespace Argon.Features.Aegis;

/// <summary>
/// Claim names this provider issues beyond the standard OIDC set.
/// </summary>
/// <remarks>
/// Written down once because they are a contract with the resource servers reading them: the admin
/// console decides what an operator may do from <see cref="OperatorId"/> and <see cref="Roles"/>,
/// and a name changed on this side is a permission silently lost on the other.
/// </remarks>
public static class AegisClaims
{
    public const string OperatorId       = "operator_id";
    public const string OperatorEmail    = "operator_email";
    public const string OperatorVerified = "operator_verified";

    /// <summary>Hex SHA-256 of the certificate the step-up was performed with.</summary>
    public const string OperatorCertThumbprint = "operator_cert_thumbprint";

    /// <summary>One per granted claim on the app; the token carries them as a JSON array.</summary>
    public const string OperatorAppClaim = "operator_app_claim";

    public const string Roles = "roles";

    /// <summary>Marks the subject as an operator rather than an ordinary account.</summary>
    public const string TokenType = "typ";

    /// <summary>Authentication method reference, RFC 8176. <c>hwk</c> — a hardware key was used.</summary>
    public const string AuthenticationMethod = "amr";

    public const string HardwareKey = "hwk";

    /// <summary>Role granted to an operator flagged as a system operator.</summary>
    public const string SystemOperatorRole = "system_operator";

    public const string DisplayName  = "displayName";
    public const string AvatarFileId = "avatarFileId";

    /// <summary>Accounts signed in on this browser, so the widget can offer a picker.</summary>
    public const string LoggedUsers = "logged_users";

    /// <summary>Set once the user has picked an account, so the picker does not loop.</summary>
    public const string AccountSelected = "account_selected";
}
