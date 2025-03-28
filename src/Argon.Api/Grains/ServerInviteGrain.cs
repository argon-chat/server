namespace Argon.Grains;

using Orleans.Concurrency;
using Shared.Servers;

[StatelessWorker]
public class ServerInviteGrain(ILogger<IServerInvitesGrain> logger, IDbContextFactory<ApplicationDbContext> context) : Grain, IServerInvitesGrain
{
    public async Task<InviteCode> CreateInviteLinkAsync(Guid issuer, TimeSpan expiration)
    {
        await using var db         = await context.CreateDbContextAsync();
        var             inviteCode = InviteCodeEntity.GenerateInviteCode();

        await db.ServerInvites.AddAsync(new ServerInvite
        {
            Id        = InviteCodeEntity.EncodeToUlong(inviteCode),
            CreatedAt = DateTime.Now,
            CreatorId = issuer,
            Expired   = DateTime.UtcNow + expiration,
            ServerId  = this.GetPrimaryKey(),
        });
        await db.SaveChangesAsync();
        return new InviteCode(inviteCode);
    }

    public async Task<List<InviteCodeEntity>> GetInviteCodes()
    {
        await using var db = await context.CreateDbContextAsync();

        var list = await db.ServerInvites
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .AsNoTracking()
           .ToListAsync();
        return list.Select(x => new InviteCodeEntity(new InviteCode(InviteCodeEntity.DecodeFromUlong(x.Id)), x.ServerId, x.CreatorId, x.Expired, 0)).ToList();
    }
}