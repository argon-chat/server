namespace Argon.Grains;

using Argon.Core.Services;
using Argon.Features.Invites;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;
using InviteCode = Entities.InviteCode;

[StatelessWorker]
public class ServerInviteGrain(
    ILogger<IServerInvitesGrain>            logger,
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker                     entitlementChecker,
    HybridCache                             cache) : Grain, IServerInvitesGrain
{
    /// <summary>
    /// Space invites are space administration: the client offers the invites page to ManageServer only.
    /// A voice-room link needs only the right to walk into that room.
    /// </summary>
    private async Task<bool> IsAllowedAsync(Guid callerId, Guid? channelId = null)
    {
        var spaceId = this.GetPrimaryKey();
        return channelId is { } room
            ? await entitlementChecker.HasChannelAccessAsync(spaceId, room, callerId, ArgonEntitlement.Connect)
            : await entitlementChecker.HasAccessAsync(spaceId, callerId, ArgonEntitlement.ManageServer);
    }

    public async Task<(SpaceManageError error, InviteCode code)> CreateInviteLinkAsync(Guid issuer, TimeSpan expiration, int maxUses, Guid? channelId = null)
    {
        if (!await IsAllowedAsync(issuer, channelId))
            return (SpaceManageError.NO_PERMISSION, default);

        await using var db         = await context.CreateDbContextAsync();
        var             inviteCode = InviteCodeEntityData.GenerateInviteCode();

        await db.Invites.AddAsync(new SpaceInvite
        {
            Id        = InviteCodeEntityData.EncodeToUlong(inviteCode),
            CreatedAt = DateTime.UtcNow,
            CreatorId = issuer,
            UpdatedAt = DateTime.UtcNow,
            ExpireAt  = DateTime.UtcNow + expiration,
            SpaceId   = this.GetPrimaryKey(),
            MaxUses   = maxUses < 0 ? 0 : maxUses,
            UsedCount = 0,
            ChannelId = channelId,
        });
        await db.SaveChangesAsync();
        return (SpaceManageError.NONE, new InviteCode(inviteCode));
    }

    public async Task<(SpaceManageError error, List<InviteCodeEntityData> codes)> GetInviteCodes(Guid callerId)
    {
        if (!await IsAllowedAsync(callerId))
            return (SpaceManageError.NO_PERMISSION, []);

        await using var db = await context.CreateDbContextAsync();

        var list = await db.Invites
           .Where(x => x.SpaceId == this.GetPrimaryKey())
           .AsNoTracking()
           .ToListAsync();
        return (SpaceManageError.NONE, list.Select(x => new InviteCodeEntityData(
            new InviteCode(InviteCodeEntityData.DecodeFromUlong(x.Id)),
            x.SpaceId, x.CreatorId, x.ExpireAt, x.UsedCount, x.MaxUses, x.CreatedAt, x.ChannelId)).ToList());
    }

    public async Task<SpaceManageError> RevokeInviteAsync(Guid callerId, string inviteCode)
    {
        if (!await IsAllowedAsync(callerId))
            return SpaceManageError.NO_PERMISSION;

        if (!InviteCodeEntityData.TryParseInviteCode(inviteCode, out var inviteId) || inviteId is null)
            return SpaceManageError.NONE;

        await using var db = await context.CreateDbContextAsync();
        await db.Invites
           .Where(x => x.Id == inviteId.Value && x.SpaceId == this.GetPrimaryKey())
           .ExecuteDeleteAsync();

        // The public card for this code is cached for a day — an invite row is written once and
        // then only ever deleted, so this delete is the single act that can make that cached answer
        // a lie, and therefore the single place that has to drop it.
        await InviteCardCache.InvalidateAsync(cache, inviteCode);
        return SpaceManageError.NONE;
    }
}