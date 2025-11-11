namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.IAppsManagementGrain")]
public interface IAppsManagementGrain : IGrainWithGuidKey
{
    //[Alias(nameof(CreateTeamAsync))]
    //Task<Guid> CreateTeamAsync(Guid ownerId, string name, CancellationToken ct = default);

    [Alias("GetCredentialsForBotAsync")]
    Task<BotCredentialsInfo?> GetCredentialsForBotAsync(string clientId, CancellationToken ct = default);
}

public record BotCredentialsInfo(
    string ClientId,
    string ClientSecret,
    List<string> allowedRedirects,
    List<string> scopes,
    bool IsAllowedRefreshToken);

