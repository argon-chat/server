namespace Argon.Core.Features.Logic;

using ArgonContracts;

public interface IBadgeAggregationService
{
    Task<GlobalBadges> GetGlobalBadgesAsync(Guid userId, CancellationToken ct = default);
}
