namespace Argon.Grains;

using Argon.Core.Features.Transport;
using Argon.Entities;
using Grains.Interfaces;
using ion.runtime;

public class SpaceBoostGrain(
    IDbContextFactory<ApplicationDbContext> context,
    AppHubServer appHubServer,
    ILogger<ISpaceBoostGrain> logger) : Grain, ISpaceBoostGrain
{
    private static readonly int[] BoostLevelThresholds = [0, 3, 7, 14];

    private Guid SpaceId => this.GetPrimaryKey();

    public async Task<SpaceBoostStatus> GetBoostStatusAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var boosters = await ctx.SpaceBoosts
           .AsNoTracking()
           .Include(x => x.User)
           .Where(x => x.SpaceId == SpaceId)
           .GroupBy(x => new { x.UserId, x.User.Username })
           .Select(g => new SpaceBooster(g.Key.UserId, g.Key.Username, g.Count()))
           .ToListAsync(ct);

        var boostCount = boosters.Sum(x => x.boostCount);
        var boostLevel = CalculateLevel(boostCount);

        return new SpaceBoostStatus(boostCount, boostLevel, new IonArray<SpaceBooster>(boosters));
    }

    public async Task RecalculateAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var boostCount = await ctx.SpaceBoosts
           .AsNoTracking()
           .CountAsync(x => x.SpaceId == SpaceId, ct);

        var boostLevel = CalculateLevel(boostCount);

        await ctx.Spaces
           .Where(x => x.Id == SpaceId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(x => x.BoostCount, boostCount)
               .SetProperty(x => x.BoostLevel, boostLevel), ct);

        await appHubServer.BroadcastSpace(
            new SpaceBoostUpdated(SpaceId, boostCount, boostLevel), SpaceId, ct);

        logger.LogInformation("Space {SpaceId} boost recalculated: count={Count}, level={Level}", SpaceId, boostCount, boostLevel);
    }

    public async Task AddBoostAsync(Guid userId, Guid boostEntityId, CancellationToken ct = default)
        => await RecalculateAsync(ct);

    public async Task RemoveBoostAsync(Guid boostEntityId, CancellationToken ct = default)
        => await RecalculateAsync(ct);

    private static int CalculateLevel(int boostCount)
    {
        for (var i = BoostLevelThresholds.Length - 1; i >= 0; i--)
        {
            if (boostCount >= BoostLevelThresholds[i])
                return i;
        }
        return 0;
    }
}
