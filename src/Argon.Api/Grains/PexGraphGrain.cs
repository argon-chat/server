namespace Argon.Grains;

using Microsoft.EntityFrameworkCore.Internal;
using Orleans.Concurrency;

[StatelessWorker]
public class PexGraphGrain(IDbContextFactory<ApplicationDbContext> context) : IPexGraphGrain
{
    public async Task<bool> HasAccessTo(Guid userId, PexGraphScope obj, ArgonEntitlement targetCheck)
    {
        await using var ctx = await context.CreateDbContextAsync();

        //EntitlementEvaluator.HasAccessTo(await ctx.UsersToServerRelations.FirstAsync(x => x.ServerId == obj.ServerId && x.UserId == userId), )

        return false;

    }
}