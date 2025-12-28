namespace Argon.Core.Services;

using Microsoft.EntityFrameworkCore;

public interface IEntitlementChecker
{
    Task<bool> HasAccessAsync(
        ApplicationDbContext ctx, 
        Guid spaceId, 
        Guid callerId, 
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default);
}

public class EntitlementChecker : IEntitlementChecker
{
    public async Task<bool> HasAccessAsync(
        ApplicationDbContext ctx, 
        Guid spaceId, 
        Guid callerId, 
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default)
    {
        var hasAccess = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.SpaceId == spaceId && x.UserId == callerId)
           .SelectMany(x => x.SpaceMemberArchetypes)
           .Select(x => x.Archetype.Entitlement)
           .AnyAsync(e => (e & requiredEntitlement) == requiredEntitlement, ct);

        return hasAccess;
    }
}
