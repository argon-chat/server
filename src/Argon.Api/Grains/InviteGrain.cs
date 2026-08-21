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

    public async ValueTask<(InviteTarget?, AcceptInviteError)> PreviewAsync()
    {
        if (!InviteCodeEntityData.TryParseInviteCode(this.GetPrimaryKeyString(), out var inviteId) || inviteId is null)
            return (null, AcceptInviteError.NOT_FOUND);

        await using var db = await context.CreateDbContextAsync();

        var invite = await db.Invites
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.Id == inviteId.Value);

        if (invite is null)
            return (null, AcceptInviteError.NOT_FOUND);
        if (invite.ExpireAt < DateTimeOffset.UtcNow)
            return (null, AcceptInviteError.EXPIRED);
        if (invite.MaxUses > 0 && invite.UsedCount >= invite.MaxUses)
            return (null, AcceptInviteError.LIMIT_REACHED);

        if (invite.ChannelId is not { } channelId)
            return (new InviteTarget(invite.SpaceId), AcceptInviteError.NONE);

        // A room that was deleted since the link was minted degrades the invite to a plain space
        // invite rather than breaking it — the space is still a valid place to land.
        var channel = await db.Channels
           .AsNoTracking()
           .Where(c => c.Id == channelId && c.SpaceId == invite.SpaceId && !c.IsDeleted)
           .Select(c => new { c.Name })
           .FirstOrDefaultAsync();

        return channel is null
            ? (new InviteTarget(invite.SpaceId), AcceptInviteError.NONE)
            : (new InviteTarget(invite.SpaceId, channelId, channel.Name), AcceptInviteError.NONE);
    }

    public async ValueTask DropInviteCodeAsync()
    {
        // TODO
    }
}