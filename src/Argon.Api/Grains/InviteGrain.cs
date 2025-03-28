namespace Argon.Grains;

using Orleans.Concurrency;
using Shared.Servers;

[StatelessWorker]
public class InviteGrain(IDbContextFactory<ApplicationDbContext> context) : Grain, IInviteGrain
{
    public async ValueTask<(Guid, AcceptInviteError)> AcceptAsync(Guid userId)
    {
        await using var db = await context.CreateDbContextAsync();
        
        var e = await db.ServerInvites.FirstOrDefaultAsync(x => x.Id == InviteCodeEntity.EncodeToUlong(this.GetPrimaryKeyString()));

        if (e is null)
            return (Guid.Empty, AcceptInviteError.NOT_FOUND); // TODO
        if (e.Expired < DateTime.Now)
            return (Guid.Empty, AcceptInviteError.EXPIRED);
        await GrainFactory.GetGrain<IServerGrain>(e.ServerId).DoJoinUserAsync(userId);
        return (e.ServerId, AcceptInviteError.NONE);
    }

    public async ValueTask DropInviteCodeAsync()
    {
        // TODO
    }

}