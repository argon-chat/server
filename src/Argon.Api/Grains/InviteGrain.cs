namespace Argon.Grains;

using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;

[StatelessWorker(maxLocalWorkers: 1024)]
public class InviteGrain(IDbContextFactory<ApplicationDbContext> context, HybridCache cache) : Grain, IInviteGrain
{
    public async ValueTask<(Guid, AcceptInviteError)> AcceptAsync()
    {
        var e = await cache.GetOrCreateAsync<SpaceInvite?>(
            string.Format(InviteCodeEntityData.CacheEntityKey, this.GetPrimaryKeyString()), async token =>
        {
            await using var db = await context.CreateDbContextAsync(token);

            var entity = await db.Invites.AsNoTracking().FirstOrDefaultAsync(
                x => x.Id == InviteCodeEntityData.EncodeToUlong(this.GetPrimaryKeyString()),
                token);
            return entity;
        });

        if (e is null)
            return (Guid.Empty, AcceptInviteError.NOT_FOUND);
        if (e.ExpireAt < DateTime.Now)
            return (Guid.Empty, AcceptInviteError.EXPIRED);
        await GrainFactory.GetGrain<ISpaceGrain>(e.SpaceId).DoJoinUserAsync();
        return (e.SpaceId, AcceptInviteError.NONE);
    }

    public async ValueTask DropInviteCodeAsync()
    {
        // TODO
    }
}