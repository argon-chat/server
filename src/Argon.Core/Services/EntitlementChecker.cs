namespace Argon.Core.Services;

using Argon.ArchetypeModel;
using Argon.Services.L1L2;
using Microsoft.EntityFrameworkCore;

public interface IEntitlementChecker
{
    Task<bool> HasAccessAsync(
        ApplicationDbContext ctx, 
        Guid spaceId, 
        Guid callerId, 
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default);

    Task<bool> HasAccessAsync(
        Guid spaceId,
        Guid callerId,
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default);

    Task<bool> HasChannelAccessAsync(
        Guid spaceId,
        Guid channelId,
        Guid callerId,
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default);
}

public class EntitlementChecker(IPermissionCache permissionCache) : IEntitlementChecker
{
    public async Task<bool> HasAccessAsync(
        ApplicationDbContext ctx, 
        Guid spaceId, 
        Guid callerId, 
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default)
    {
        var permissions = await permissionCache.GetBasePermissionsAsync(spaceId, callerId, ct);
        return EntitlementAnalyzer.IsEntitlementSatisfied(permissions, requiredEntitlement);
    }

    public async Task<bool> HasAccessAsync(
        Guid spaceId,
        Guid callerId,
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default)
    {
        var permissions = await permissionCache.GetBasePermissionsAsync(spaceId, callerId, ct);
        return EntitlementAnalyzer.IsEntitlementSatisfied(permissions, requiredEntitlement);
    }

    public async Task<bool> HasChannelAccessAsync(
        Guid spaceId,
        Guid channelId,
        Guid callerId,
        ArgonEntitlement requiredEntitlement,
        CancellationToken ct = default)
    {
        var member = await permissionCache.GetMemberWithArchetypesAsync(spaceId, callerId, ct);
        if (member is null)
            return false;

        var channel = await permissionCache.GetChannelWithOverwritesAsync(channelId, ct);
        if (channel is null)
            return false;

        return EntitlementEvaluator.HasAccessTo(member, channel, requiredEntitlement);
    }
}
