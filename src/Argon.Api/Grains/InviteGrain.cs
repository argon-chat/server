namespace Argon.Grains;

using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;
using Shared.Servers;

[StatelessWorker(1024)]
public class InviteGrain(IDbContextFactory<ApplicationDbContext> context, HybridCache cache) : Grain, IInviteGrain
{
    public async ValueTask<(Guid, AcceptInviteError)> AcceptAsync()
    {
        var e = await cache.GetOrCreateAsync<ServerInviteDto?>(
            string.Format(InviteCodeEntity.CacheEntityKey, this.GetPrimaryKeyString()), async token =>
        {
            await using var db = await context.CreateDbContextAsync(token);

            var entity = await db.ServerInvites.AsNoTracking().FirstOrDefaultAsync(
                x => x.Id == InviteCodeEntity.EncodeToUlong(this.GetPrimaryKeyString()),
                token);
            return entity?.ToDto();
        });

        if (e is null)
            return (Guid.Empty, AcceptInviteError.NOT_FOUND);
        if (e.Expired < DateTime.Now)
            return (Guid.Empty, AcceptInviteError.EXPIRED);
        await GrainFactory.GetGrain<IServerGrain>(e.ServerId).DoJoinUserAsync();
        return (e.ServerId, AcceptInviteError.NONE);
    }

    public async ValueTask DropInviteCodeAsync()
    {
        // TODO
    }
}