namespace Argon.Grains;

using Orleans.Concurrency;
using Sfu;
using Shared.Servers;

[StatelessWorker]
public class MeetGrain(IDbContextFactory<ApplicationDbContext> context, IArgonSelectiveForwardingUnit sfu) : Grain, IMeetGrain
{
    public async ValueTask<Either<MeetAuthorizationData, MeetJoinError>> AcceptAsync(string Name)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var id = InviteCodeEntity.EncodeToUlong(this.GetPrimaryKeyString());

        var inviteCode = ctx.MeetInviteLinks.FirstOrDefaultAsync(x => x.Id.Equals(id));


        throw null;
    }
}