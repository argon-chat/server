namespace Argon.Contracts.Models.ArchetypeModel;

public static class EntitlementEvaluator
{
    public static ArgonEntitlement CalculatePermissions(ServerMember member, Guid serverId)
    {
        if (member.ServerId != serverId)
            return ArgonEntitlement.None;

        var permissions = GetBasePermissions(member);

        if (permissions.HasFlag(ArgonEntitlement.Administrator))
            return ArgonEntitlement.Administrator;
        return member.ServerMemberArchetypes.Aggregate(ArgonEntitlement.None, (current, smr) => current | smr.Archetype.Entitlement);
    }

    public static ArgonEntitlement CalculatePermissions(ServerMember member, Channel channel)
    {
        var permissions = GetBasePermissions(member);

        if (permissions.HasFlag(ArgonEntitlement.Administrator))
            return ArgonEntitlement.Administrator;

        permissions = ApplyPermissionOverwrites(permissions, member, channel);

        return permissions;
    }

    public static ArgonEntitlement GetBasePermissions(ServerMember member)
        => member.ServerMemberArchetypes.Aggregate(ArgonEntitlement.None, (current, smr) => current | smr.Archetype.Entitlement);

    public static ArgonEntitlement ApplyPermissionOverwrites(ArgonEntitlement permissions, ServerMember member, Channel channel)
    {
        var roleOverwrites = channel.EntitlementOverwrites
           .Where(po => po.Scope == IArchetypeScope.Archetype)
           .Where(po => member.ServerMemberArchetypes.Any(smr => smr.ArchetypeId == po.ArchetypeId))
           .ToList();

        foreach (var overwrite in roleOverwrites)
        {
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        var overwrites = channel.EntitlementOverwrites
           .Where(po => po.Scope == IArchetypeScope.Member)
           .FirstOrDefault(po => po.ServerMemberId == member.Id);

        if (overwrites == null) 
            return permissions;

        permissions &= ~overwrites.Deny;
        permissions |= overwrites.Allow;

        return permissions;
    }
}