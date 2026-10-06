namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.IAppsManagementGrain")]
public interface IAppsManagementGrain : IGrainWithGuidKey
{
    //[Alias(nameof(CreateTeamAsync))]
    //Task<Guid> CreateTeamAsync(Guid ownerId, string name, CancellationToken ct = default);

    [Alias("GetCredentialsForBotAsync")]
    Task<BotCredentialsInfo?> GetCredentialsForBotAsync(string clientId, CancellationToken ct = default);

    [Alias("CanBeLoginForAppAsync")]
    Task<LoginAllowedResult> CanBeLoginForAppAsync(string clientId, Guid userId, CancellationToken ct = default);

    [Alias("GetOAuthAppInfoAsync")]
    Task<OAuthAppInfo?> GetOAuthAppInfoAsync(string clientId, IReadOnlyList<string> requestedScopes, CancellationToken ct = default);
}

public record BotCredentialsInfo(
    string ClientId,
    string ClientSecret,
    List<string> allowedRedirects,
    List<string> scopes,
    bool IsAllowedRefreshToken,
    bool AllowMagicLink);

public record LoginAllowedResult(bool IsAllowed, string? Reason);

/// <summary>
/// Codes for <see cref="LoginAllowedResult.Reason"/> and the operator refusals; the sign-in widget translates them.
/// </summary>
public static class LoginDenial
{
    public const string AppNotFound           = "app_not_found";
    public const string UnapprovedAppTeamOnly = "unapproved_app_team_only";
    public const string InternalAppTeamOnly   = "internal_app_team_only";
    public const string InternalAppStaffOnly  = "internal_app_staff_only";
    public const string OperatorMissing       = "operator_missing";
    public const string OperatorInactive      = "operator_inactive";
    public const string OperatorNoAppAccess   = "operator_no_app_access";
}

/// <summary>
/// OAuth consent screen information.
/// </summary>
public record OAuthAppInfo(
    Guid AppId,
    string AppName,
    string? AppDescription,
    string? AppAvatarFileId,
    string DeveloperName,
    string? WebsiteUrl,
    bool IsVerified,
    bool IsInternalApp,
    IReadOnlyList<string> RequestedScopes);