namespace Argon.Grains;

using Orleans.Concurrency;
using InviteCode = Entities.InviteCode;

[StatelessWorker]
public class ServerInviteGrain(ILogger<IServerInvitesGrain> logger, IDbContextFactory<ApplicationDbContext> context) : Grain, IServerInvitesGrain
{
    public async Task<InviteCode> CreateInviteLinkAsync(Guid issuer, TimeSpan expiration)
    {
        await using var db         = await context.CreateDbContextAsync();
        var             inviteCode = InviteCodeEntityData.GenerateInviteCode();

        await db.Invites.AddAsync(new SpaceInvite
        {
            Id        = InviteCodeEntityData.EncodeToUlong(inviteCode),
            CreatedAt = DateTime.Now,
            CreatorId = issuer,
            ExpireAt   = DateTime.UtcNow + expiration,
            SpaceId  = this.GetPrimaryKey(),
        });
        await db.SaveChangesAsync();
        return new InviteCode(inviteCode);
    }

    public async Task<List<InviteCodeEntityData>> GetInviteCodes()
    {
        await using var db = await context.CreateDbContextAsync();

        var list = await db.Invites
           .Where(x => x.SpaceId == this.GetPrimaryKey())
           .AsNoTracking()
           .ToListAsync();
        return list.Select(x => new InviteCodeEntityData(new InviteCode(InviteCodeEntityData.DecodeFromUlong(x.Id)), x.SpaceId, x.CreatorId, x.ExpireAt, 0)).ToList();
    }
}