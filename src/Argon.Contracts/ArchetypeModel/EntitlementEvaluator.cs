namespace Argon.ArchetypeModel;

public static class EntitlementAnalyzer
{
    private const ulong CHAT_MASK  = 0b111111111000000 << 5;
    private const ulong VOICE_MASK = 0b1111UL << 20;
    private const ulong MOD_MASK   = 0b11111UL << 40;
    private const ulong ADMIN_MASK = 0b111111UL << 50;

    public static bool HasEntitlement(ServerMember member, IArchetypeObject obj, ArgonEntitlement target)
    {
        var permissions = EntitlementEvaluator.GetBasePermissions(member);

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Archetype))
        {
            if (!overwrite.ArchetypeId.HasValue || member.ServerMemberArchetypes.All(smr => smr.ArchetypeId != overwrite.ArchetypeId.Value)) continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Member))
        {
            if (overwrite.ServerMemberId != member.Id) continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        return IsEntitlementSatisfied(permissions, target);
    }

    public static bool IsEntitlementSatisfied(ArgonEntitlement permissions, ArgonEntitlement target)
    {
        if (permissions.HasFlag(ArgonEntitlement.Administrator))
            return true;

        if (!permissions.HasFlag(target))
            return false;

        if (IsChatEntitlement(target) && !permissions.HasFlag(ArgonEntitlement.SendMessages))
            return false;

        if (IsSendMessagesEntitlement(target) && !permissions.HasFlag(ArgonEntitlement.ViewChannel))
            return false;

        if (IsVoiceEntitlement(target) && !permissions.HasFlag(ArgonEntitlement.JoinToVoice))
            return false;

        if (target == ArgonEntitlement.JoinToVoice && !permissions.HasFlag(ArgonEntitlement.ViewChannel))
            return false;

        return true;
    }

    private static bool IsChatEntitlement(ArgonEntitlement target)
        => ((ulong)target & CHAT_MASK) != 0 && target != ArgonEntitlement.SendMessages;

    private static bool IsSendMessagesEntitlement(ArgonEntitlement target)
        => target == ArgonEntitlement.SendMessages;

    private static bool IsVoiceEntitlement(ArgonEntitlement target)
        => ((ulong)target & VOICE_MASK) != 0 && target != ArgonEntitlement.JoinToVoice;
}

public static class EntitlementEvaluator
{
    public static bool IsAllowedToEdit(
        Archetype targetToEdit,
        List<Archetype> userArchetypes)
    {
        var maxEntitlement = userArchetypes.Max(x => (ulong)x.Entitlement);

        return maxEntitlement > (ulong)targetToEdit.Entitlement;
    }

    public static bool IsAllowedToEdit(
        Archetype targetToEdit,
        ArgonEntitlement promptedEntitlement,
        List<Archetype> userArchetypes)
    {
        var userPermissions = userArchetypes
           .Aggregate(ArgonEntitlement.None, (current, archetype) => current | archetype.Entitlement);

        if (userPermissions.HasFlag(ArgonEntitlement.Administrator))
            return true;

        if (userPermissions.HasFlag(ArgonEntitlement.ManageServer))
            return true;

        if (userArchetypes.Any(ua => ua.Id == targetToEdit.Id))
            return false;

        var tryingToAdd = promptedEntitlement & ~userPermissions;
        return tryingToAdd == ArgonEntitlement.None;
    }

    public static bool HasAccessTo(ServerMember member, IArchetypeObject obj, ArgonEntitlement targetCheck)
    {
        var permissions = GetBasePermissions(member);

        if (permissions.HasFlag(ArgonEntitlement.Administrator))
            return true;

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Archetype))
        {
            if (!overwrite.ArchetypeId.HasValue ||
                member.ServerMemberArchetypes.All(smr => smr.ArchetypeId != overwrite.ArchetypeId.Value))
                continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Member))
        {
            if (overwrite.ServerMemberId != member.Id)
                continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        var hasRelevantRoleOverwrite = obj.Overwrites.Any(o =>
            o.Scope == IArchetypeScope.Archetype &&
            (o.Allow.HasFlag(targetCheck) || o.Deny.HasFlag(targetCheck)));

        var userHasMatchingRoleOverwrite = obj.Overwrites.Any(o =>
            o is { Scope: IArchetypeScope.Archetype, ArchetypeId: not null } &&
            (o.Allow.HasFlag(targetCheck) || o.Deny.HasFlag(targetCheck)) &&
            member.ServerMemberArchetypes.Any(smr => smr.ArchetypeId == o.ArchetypeId.Value));

        if (hasRelevantRoleOverwrite && !userHasMatchingRoleOverwrite)
            return false;

        return EntitlementAnalyzer.IsEntitlementSatisfied(permissions, targetCheck);
    }

    public static ArgonEntitlement CalculatePermissions(ServerMember member, Server server)
    {
        if (member.ServerId != server.Id)
            return ArgonEntitlement.None;

        var permissions = GetBasePermissions(member);

        if (permissions.HasFlag(ArgonEntitlement.Administrator))
            return ArgonEntitlement.Administrator;
        return member.ServerMemberArchetypes.Aggregate(ArgonEntitlement.None, (current, smr) => current | smr.Archetype.Entitlement);
    }

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