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
        var permissions = EntitlementEvaluator.ApplyPermissionOverwrites(EntitlementEvaluator.GetBasePermissions(member), member, obj);

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

        // A room the member cannot see is not one they can join. Not JoinToVoice: no role editor or kit
        // grants it, so requiring it locked every non-admin out of voice.
        if (IsVoiceEntitlement(target) && !permissions.HasFlag(ArgonEntitlement.ViewChannel))
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

    /// <summary>
    /// Every right in <paramref name="required"/>, each judged on its own: the rules below are written
    /// for one right at a time (an opt-in channel, the rights one presupposes), so a combined value
    /// cannot be judged as one.
    /// </summary>
    public static bool HasAccessToAll(SpaceMemberEntity member, IArchetypeObject obj, ArgonEntitlement required)
    {
        var basePermissions = GetBasePermissions(member);
        var bits            = (ulong)required;

        if (System.Numerics.BitOperations.PopCount(bits) <= 1)
            return HasAccessTo(basePermissions, member, obj, required);

        for (; bits != 0; bits &= bits - 1)
        {
            if (!HasAccessTo(basePermissions, member, obj, (ArgonEntitlement)(bits & (~bits + 1))))
                return false;
        }

        return true;
    }

    public static bool HasAccessTo(ArgonEntitlement basePermissions, SpaceMemberEntity member, IArchetypeObject obj,
        ArgonEntitlement targetCheck)
    {
        if (basePermissions.HasFlag(ArgonEntitlementKit.Administrator))
            return true;

        var permissions = ApplyPermissionOverwrites(basePermissions, member, obj);

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

    /// <summary>
    /// A channel's overwrites on top of the base permissions, the same whatever order they were stored
    /// in: "everyone" first, then every other role the member holds at once, so an allow on one beats a
    /// deny on another, then the member's own.
    /// </summary>
    public static ArgonEntitlement ApplyPermissionOverwrites(ArgonEntitlement permissions, SpaceMemberEntity member, IArchetypeObject obj)
    {
        var everyone  = member.SpaceMemberArchetypes.FirstOrDefault(smr => smr.Archetype?.IsDefault == true)?.ArchetypeId;
        var roleDeny  = ArgonEntitlement.None;
        var roleAllow = ArgonEntitlement.None;

        foreach (var overwrite in obj.Overwrites)
        {
            if (overwrite.Scope != IArchetypeScope.Archetype || overwrite.ArchetypeId is not { } archetypeId
             || member.SpaceMemberArchetypes.All(smr => smr.ArchetypeId != archetypeId))
                continue;

            if (archetypeId == everyone)
            {
                permissions &= ~overwrite.Deny;
                permissions |= overwrite.Allow;
            }
            else
            {
                roleDeny  |= overwrite.Deny;
                roleAllow |= overwrite.Allow;
            }
        }

        permissions &= ~roleDeny;
        permissions |= roleAllow;

        var overwrites = obj.Overwrites
           .FirstOrDefault(po => po.Scope == IArchetypeScope.Member && po.SpaceMemberId == member.Id);

        if (overwrites == null)
            return permissions;

        permissions &= ~overwrites.Deny;
        permissions |= overwrites.Allow;

        return permissions;
    }
}