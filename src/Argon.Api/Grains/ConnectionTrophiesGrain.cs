namespace Argon.Grains;

using Argon.Api.Grains.Interfaces;
using Argon.Features.EF;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Providers;
using Argon.Grains.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;

/// <inheritdoc cref="IConnectionTrophiesGrain"/>
[StatelessWorker]
public sealed class ConnectionTrophiesGrain(
    IDbContextFactory<ApplicationDbContext> context,
    GitHubConnectionProvider github,
    HybridCache cache,
    IOptions<ConnectionsOptions> options,
    ILogger<ConnectionTrophiesGrain> logger) : Grain, IConnectionTrophiesGrain
{
    private const string ContributorsKey = "conn:github:contributors";

    /// <summary>
    /// Six hours: a merge shows up in the listing well within the weekly refresh that re-checks
    /// every GitHub row, and the listing costs one request per hundred contributors per repository.
    /// </summary>
    private static readonly HybridCacheEntryOptions ContributorsCache = new()
    {
        Expiration           = TimeSpan.FromHours(6),
        LocalCacheExpiration = TimeSpan.FromHours(1)
    };

    public async Task<bool> CheckGitHubContributorAsync(Guid userId, string gitHubId, CancellationToken ct = default)
    {
        var settings = options.Value.GitHub;

        if (!github.CanListContributors || !long.TryParse(gitHubId, out var id))
            return false;

        var contributors = await cache.GetOrCreateAsync(ContributorsKey, github,
            static async ValueTask<long[]> (g, token) => (await g.ListContributorIdsAsync(token)).ToArray(),
            ContributorsCache, cancellationToken: ct);

        if (!contributors.Contains(id))
            return false;

        await using var ctx = await context.CreateDbContextAsync(ct);

        ctx.ConnectionTrophyGrants.Add(new ConnectionTrophyGrantEntity
        {
            Provider   = ConnectionProvider.GITHUB,
            ExternalId = gitHubId,
            TrophyId   = settings.ContributorCoin,
            UserId     = userId,
            GrantedAt  = DateTimeOffset.UtcNow
        });

        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            // This GitHub identity has been paid already, whichever Argon account held it then.
            return false;
        }

        await GrainFactory.GetGrain<IInventoryGrain>(userId).GiveCoinFor(userId, settings.ContributorCoin, settings.ContributorBadge, ct);

        logger.LogInformation("Contributor coin {Coin} granted to {UserId} for GitHub {GitHubId}", settings.ContributorCoin, userId, gitHubId);

        return true;
    }
}
