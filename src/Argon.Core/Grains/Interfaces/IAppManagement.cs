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
    bool IsAllowedRefreshToken);

public record LoginAllowedResult(bool IsAllowed, string? Reason);

/// <summary>
/// OAuth consent screen information.
/// </summary>
public record OAuthAppInfo(
    string AppName,
    string? AppDescription,
    string? AppAvatarFileId,
    string DeveloperName,
    string? WebsiteUrl,
    bool IsVerified,
    IReadOnlyList<string> RequestedScopes);