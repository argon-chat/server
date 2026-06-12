namespace Argon.Grains;

using Orleans.Concurrency;

[StatelessWorker(maxLocalWorkers: 1024)]
public class InviteGrain(IDbContextFactory<ApplicationDbContext> context) : Grain, IInviteGrain
{
    public async ValueTask<(Guid, AcceptInviteError)> AcceptAsync()
    {
        if (!InviteCodeEntityData.TryParseInviteCode(this.GetPrimaryKeyString(), out var inviteId) || inviteId is null)
            return (Guid.Empty, AcceptInviteError.NOT_FOUND);

        await using var db = await context.CreateDbContextAsync();

        var invite = await db.Invites
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.Id == inviteId.Value);

        if (invite is null)
            return (Guid.Empty, AcceptInviteError.NOT_FOUND);
        if (invite.ExpireAt < DateTimeOffset.UtcNow)
            return (Guid.Empty, AcceptInviteError.EXPIRED);
        if (invite.MaxUses > 0 && invite.UsedCount >= invite.MaxUses)
            return (Guid.Empty, AcceptInviteError.LIMIT_REACHED);

        // Record which invite the member joined through. Idempotent: returns false if already a member.
        var joined = await GrainFactory.GetGrain<ISpaceGrain>(invite.SpaceId).DoJoinUserAsync(invite.Id);

        if (joined)
        {
            // Atomic, race-safe increment guarded by the usage limit.
            await db.Invites
               .Where(x => x.Id == invite.Id && (x.MaxUses == 0 || x.UsedCount < x.MaxUses))
               .ExecuteUpdateAsync(s => s.SetProperty(p => p.UsedCount, p => p.UsedCount + 1));
        }

        return (invite.SpaceId, AcceptInviteError.NONE);
    }

    public async ValueTask<(Guid, AcceptInviteError)> PreviewAsync()
    {
        if (!InviteCodeEntityData.TryParseInviteCode(this.GetPrimaryKeyString(), out var inviteId) || inviteId is null)
            return (Guid.Empty, AcceptInviteError.NOT_FOUND);

        await using var db = await context.CreateDbContextAsync();

        var invite = await db.Invites
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.Id == inviteId.Value);

        if (invite is null)
            return (Guid.Empty, AcceptInviteError.NOT_FOUND);
        if (invite.ExpireAt < DateTimeOffset.UtcNow)
            return (Guid.Empty, AcceptInviteError.EXPIRED);
        if (invite.MaxUses > 0 && invite.UsedCount >= invite.MaxUses)
            return (Guid.Empty, AcceptInviteError.LIMIT_REACHED);

        return (invite.SpaceId, AcceptInviteError.NONE);
    }

    public async ValueTask DropInviteCodeAsync()
    {
        // TODO
    }
}