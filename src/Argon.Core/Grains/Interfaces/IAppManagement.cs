namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.IAppsManagementGrain")]
public interface IAppsManagementGrain : IGrainWithGuidKey
{
    //[Alias(nameof(CreateTeamAsync))]
    //Task<Guid> CreateTeamAsync(Guid ownerId, string name, CancellationToken ct = default);

    [Alias("GetScopesForBotAsync")]
    Task<Either<List<string>, GetScopesForBotError>> GetScopesForBotAsync(string clientId, string clientSecret, string redirect, CancellationToken ct = default);
}


public enum GetScopesForBotError
{
    NoBotFound,
    BadSecret,
    BadRedirectsUrls
}