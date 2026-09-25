namespace Argon.ArchetypeModel;

public static class EntitlementAnalyzer
{
    private const ulong CHAT_MASK  = 0b111111111000000 << 5;
    private const ulong VOICE_MASK = 0b1111UL << 20;
    private const ulong MOD_MASK   = 0b1111111111UL << 40;
    // Seven bits, not six: ManageMessages (1 << 56) had to go above ManageServer because the
    // moderation block at 1 << 4x is full, so the admin block now runs 50..56.
    private const ulong ADMIN_MASK = 0b1111111UL << 50;

    public static bool HasEntitlement(SpaceMemberEntity member, IArchetypeObject obj, ArgonEntitlement target)
    {
        var permissions = EntitlementEvaluator.GetBasePermissions(member);

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Archetype))
        {
            if (!overwrite.ArchetypeId.HasValue || member.SpaceMemberArchetypes.All(smr => smr.ArchetypeId != overwrite.ArchetypeId.Value)) continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Member))
        {
            if (overwrite.SpaceMemberId != member.Id) continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        return IsEntitlementSatisfied(permissions, target);
    }

    public static bool IsEntitlementSatisfied(ArgonEntitlement permissions, ArgonEntitlement target)
    {
        if (permissions.HasFlag(ArgonEntitlementKit.Administrator))
            return true;

        if (!permissions.HasFlag(target))
            return false;

        if (IsChatEntitlement(target) && !permissions.HasFlag(ArgonEntitlement.SendMessages))
            return false;

        if (IsSendMessagesEntitlement(target) && !permissions.HasFlag(ArgonEntitlement.ViewChannel))
            return false;

        if (IsVoiceEntitlement(target) && !permissions.HasFlag(ArgonEntitlement.JoinToVoice))
            return false;

        // Speaking, video and streaming are rights inside a room the member may connect to.
        if (IsVoiceEntitlement(target) && target != ArgonEntitlement.Connect && !permissions.HasFlag(ArgonEntitlement.Connect))
            return false;

        if (target == ArgonEntitlement.JoinToVoice && !permissions.HasFlag(ArgonEntitlement.ViewChannel))
            return false;

        // Outside VOICE_MASK: transmitting on a radio needs the right to speak in the room.
        if (target == ArgonEntitlement.Broadcast && !IsEntitlementSatisfied(permissions, ArgonEntitlement.Speak))
            return false;

        return true;
    }

    private static bool IsChatEntitlement(ArgonEntitlement target)
        => ((ulong)target & CHAT_MASK) != 0 && target != ArgonEntitlement.SendMessages;

    private static bool IsSendMessagesEntitlement(ArgonEntitlement target)
        => target == ArgonEntitlement.SendMessages;

    private static bool IsVoiceEntitlement(ArgonEntitlement target)
        => ((ulong)target & VOICE_MASK) != 0 && target != ArgonEntitlement.JoinToVoice;

    /// <summary>Moderation and management: in a channel they need the channel to be visible too.</summary>
    public static bool IsManagementEntitlement(ArgonEntitlement target)
        => ((ulong)target & (MOD_MASK | ADMIN_MASK)) != 0;
}

public static class EntitlementEvaluator
{
    public static bool IsAllowedToEdit(
        ArchetypeEntity targetToEdit,
        List<ArchetypeEntity> userArchetypes)
    {
        var userPermissions = userArchetypes
           .Aggregate(ArgonEntitlement.None, (current, archetype) => current | archetype.Entitlement);

        if (userPermissions.HasFlag(ArgonEntitlementKit.Administrator))
            return true;

        var targetEntitlement = targetToEdit.Entitlement;
        return (userPermissions & targetEntitlement) == targetEntitlement;
    }

    public static bool IsAllowedToEdit(
        ArchetypeEntity targetToEdit,
        ArgonEntitlement promptedEntitlement,
        List<ArchetypeEntity> userArchetypes)
    {
        var userPermissions = userArchetypes
           .Aggregate(ArgonEntitlement.None, (current, archetype) => current | archetype.Entitlement);

        if (userPermissions.HasFlag(ArgonEntitlementKit.Administrator))
            return true;

        if (userPermissions.HasFlag(ArgonEntitlement.ManageServer))
            return true;

        if (userArchetypes.Any(ua => ua.Id == targetToEdit.Id))
            return false;

        var tryingToAdd = promptedEntitlement & ~userPermissions;
        return tryingToAdd == ArgonEntitlement.None;
    }

    public static bool HasAccessTo(SpaceMemberEntity member, IArchetypeObject obj, ArgonEntitlement targetCheck)
        => HasAccessTo(GetBasePermissions(member), member, obj, targetCheck);

    public static bool HasAccessTo(ArgonEntitlement basePermissions, SpaceMemberEntity member, IArchetypeObject obj,
        ArgonEntitlement targetCheck)
    {
        var permissions = basePermissions;

        if (permissions.HasFlag(ArgonEntitlementKit.Administrator))
            return true;

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Archetype))
        {
            if (!overwrite.ArchetypeId.HasValue ||
                member.SpaceMemberArchetypes.All(smr => smr.ArchetypeId != overwrite.ArchetypeId.Value))
                continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        foreach (var overwrite in obj.Overwrites.Where(o => o.Scope == IArchetypeScope.Member))
        {
            if (overwrite.SpaceMemberId != member.Id)
                continue;
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        // Only an Allow makes a channel opt-in for that entitlement. A Deny for a role the member does
        // not hold must not affect them — otherwise a "Muted: deny SendMessages" role locks everyone out.
        var hasRelevantRoleOverwrite = obj.Overwrites.Any(o =>
            o.Scope == IArchetypeScope.Archetype && o.Allow.HasFlag(targetCheck));

        var userHasMatchingRoleOverwrite = obj.Overwrites.Any(o =>
            o is { Scope: IArchetypeScope.Archetype, ArchetypeId: not null } &&
            o.Allow.HasFlag(targetCheck) &&
            member.SpaceMemberArchetypes.Any(smr => smr.ArchetypeId == o.ArchetypeId.Value));

        if (hasRelevantRoleOverwrite && !userHasMatchingRoleOverwrite)
            return false;

        // Nobody manages or moderates a channel they cannot see.
        if (EntitlementAnalyzer.IsManagementEntitlement(targetCheck)
         && !EntitlementAnalyzer.IsEntitlementSatisfied(permissions, ArgonEntitlement.ViewChannel))
            return false;

        return EntitlementAnalyzer.IsEntitlementSatisfied(permissions, targetCheck);
    }

    private static readonly ArgonEntitlement[] SingleEntitlements = Enum.GetValues<ArgonEntitlement>()
       .Where(e => System.Numerics.BitOperations.IsPow2((ulong)e))
       .ToArray();

    /// <summary>Every entitlement <see cref="HasAccessTo(ArgonEntitlement, SpaceMemberEntity, IArchetypeObject, ArgonEntitlement)"/> grants in this channel.</summary>
    public static ArgonEntitlement EffectiveEntitlements(ArgonEntitlement basePermissions, SpaceMemberEntity member, IArchetypeObject obj)
        => SingleEntitlements
           .Where(e => HasAccessTo(basePermissions, member, obj, e))
           .Aggregate(ArgonEntitlement.None, (all, e) => all | e);

    /// <summary>Every entitlement the space-level check grants.</summary>
    public static ArgonEntitlement EffectiveEntitlements(ArgonEntitlement basePermissions)
        => SingleEntitlements
           .Where(e => EntitlementAnalyzer.IsEntitlementSatisfied(basePermissions, e))
           .Aggregate(ArgonEntitlement.None, (all, e) => all | e);

    public static ArgonEntitlement CalculatePermissions(SpaceMemberEntity member, SpaceEntity server)
    {
        if (member.SpaceId != server.Id)
            return ArgonEntitlement.None;

        var permissions = GetBasePermissions(member);

        if (permissions.HasFlag(ArgonEntitlementKit.Administrator))
            return ArgonEntitlementKit.Administrator;
        return member.SpaceMemberArchetypes.Aggregate(ArgonEntitlement.None, (current, smr) => current | smr.Archetype.Entitlement);
    }

    public static ArgonEntitlement CalculatePermissions(SpaceMemberEntity member, Guid spaceId)
    {
        if (member.SpaceId != spaceId)
            return ArgonEntitlement.None;

        var permissions = GetBasePermissions(member);

        if (permissions.HasFlag(ArgonEntitlementKit.Administrator))
            return ArgonEntitlementKit.Administrator;
        return member.SpaceMemberArchetypes.Aggregate(ArgonEntitlement.None, (current, smr) => current | smr.Archetype.Entitlement);
    }

    public static ArgonEntitlement CalculatePermissions(SpaceMemberEntity member, ChannelEntity channel)
    {
        var permissions = GetBasePermissions(member);

        if (permissions.HasFlag(ArgonEntitlementKit.Administrator))
            return ArgonEntitlementKit.Administrator;

        permissions = ApplyPermissionOverwrites(permissions, member, channel);

        return permissions;
    }

    public static ArgonEntitlement GetBasePermissions(SpaceMemberEntity member)
        => member.SpaceMemberArchetypes.Aggregate(ArgonEntitlement.None, (current, smr) => current | smr.Archetype.Entitlement);

    public static ArgonEntitlement ApplyPermissionOverwrites(ArgonEntitlement permissions, SpaceMemberEntity member, ChannelEntity channel)
    {
        var roleOverwrites = channel.EntitlementOverwrites
           .Where(po => po.Scope == IArchetypeScope.Archetype)
           .Where(po => member.SpaceMemberArchetypes.Any(smr => smr.ArchetypeId == po.ArchetypeId))
           .ToList();

        foreach (var overwrite in roleOverwrites)
        {
            permissions &= ~overwrite.Deny;
            permissions |= overwrite.Allow;
        }

        var overwrites = channel.EntitlementOverwrites
           .Where(po => po.Scope == IArchetypeScope.Member)
           .FirstOrDefault(po => po.SpaceMemberId == member.Id);

        if (overwrites == null)
            return permissions;

        permissions &= ~overwrites.Deny;
        permissions |= overwrites.Allow;

        return permissions;
    }
}