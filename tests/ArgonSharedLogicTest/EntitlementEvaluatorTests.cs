namespace ArgonSharedLogicTest;

using Argon.ArchetypeModel;
using Argon.Entities;
using ArgonContracts;
using System.Drawing;

/// <summary>
/// Permission resolution is the single most security-sensitive pure function in the server: every
/// channel read, every message send and every moderation action funnels through it. These are unit
/// tests on purpose — no database, no host — so the layered rules (base archetypes → archetype
/// overwrites → member overwrites → implied-prerequisite checks) can be pinned down exhaustively
/// and in milliseconds.
/// </summary>
[TestFixture]
public class EntitlementEvaluatorTests
{
    private static readonly Guid SpaceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // ── Builders ────────────────────────────────────────────────────────────────────────────────

    private static ArchetypeEntity Archetype(ArgonEntitlement entitlement, Guid? id = null, string name = "role")
        => new()
        {
            Id          = id ?? Guid.NewGuid(),
            SpaceId     = SpaceId,
            Name        = name,
            Description = string.Empty,
            Colour      = Color.Gray,
            Entitlement = entitlement
        };

    private static SpaceMemberEntity Member(params ArchetypeEntity[] archetypes)
    {
        var member = new SpaceMemberEntity
        {
            Id      = Guid.NewGuid(),
            SpaceId = SpaceId,
            UserId  = Guid.NewGuid()
        };

        member.SpaceMemberArchetypes = archetypes
           .Select(a => new SpaceMemberArchetypeEntity
            {
                SpaceMemberId = member.Id,
                ArchetypeId   = a.Id,
                Archetype     = a
            })
           .ToList();

        return member;
    }

    private static ChannelEntitlementOverwriteEntity ArchetypeOverwrite(
        Guid archetypeId, ArgonEntitlement allow = ArgonEntitlement.None, ArgonEntitlement deny = ArgonEntitlement.None)
        => new()
        {
            Id          = Guid.NewGuid(),
            Scope       = IArchetypeScope.Archetype,
            ArchetypeId = archetypeId,
            Allow       = allow,
            Deny        = deny
        };

    private static ChannelEntitlementOverwriteEntity MemberOverwrite(
        Guid memberId, ArgonEntitlement allow = ArgonEntitlement.None, ArgonEntitlement deny = ArgonEntitlement.None)
        => new()
        {
            Id            = Guid.NewGuid(),
            Scope         = IArchetypeScope.Member,
            SpaceMemberId = memberId,
            Allow         = allow,
            Deny          = deny
        };

    private static ChannelEntity Channel(params ChannelEntitlementOverwriteEntity[] overwrites)
        => new()
        {
            Id                     = Guid.NewGuid(),
            SpaceId                = SpaceId,
            Name                   = "general",
            ChannelType            = ChannelType.Text,
            EntitlementOverwrites  = overwrites.ToList()
        };

    // ── GetBasePermissions ──────────────────────────────────────────────────────────────────────

    [Test]
    public void GetBasePermissions_WithNoArchetypes_IsNone()
        => Assert.That(EntitlementEvaluator.GetBasePermissions(Member()), Is.EqualTo(ArgonEntitlement.None));

    [Test]
    public void GetBasePermissions_UnionsEveryArchetype()
    {
        var member = Member(
            Archetype(ArgonEntitlement.ViewChannel),
            Archetype(ArgonEntitlement.SendMessages),
            Archetype(ArgonEntitlement.AddReactions));

        var permissions = EntitlementEvaluator.GetBasePermissions(member);

        Assert.That(permissions, Is.EqualTo(
            ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages | ArgonEntitlement.AddReactions));
    }

    // ── IsEntitlementSatisfied: implied prerequisites ───────────────────────────────────────────

    [Test]
    public void IsEntitlementSatisfied_Administrator_ShortCircuitsEverything()
    {
        // Administrator is checked before any prerequisite rule, so it grants even entitlements
        // whose prerequisites are missing.
        Assert.That(
            EntitlementAnalyzer.IsEntitlementSatisfied(ArgonEntitlementKit.Administrator, ArgonEntitlement.AttachFiles),
            Is.True);
    }

    [Test]
    public void IsEntitlementSatisfied_WithoutTheBitItself_IsDenied()
        => Assert.That(
            EntitlementAnalyzer.IsEntitlementSatisfied(ArgonEntitlement.ViewChannel, ArgonEntitlement.SendMessages),
            Is.False);

    [Test]
    public void IsEntitlementSatisfied_SendMessages_RequiresViewChannel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                EntitlementAnalyzer.IsEntitlementSatisfied(ArgonEntitlement.SendMessages, ArgonEntitlement.SendMessages),
                Is.False, "SendMessages without ViewChannel must not be satisfied");

            Assert.That(
                EntitlementAnalyzer.IsEntitlementSatisfied(
                    ArgonEntitlement.SendMessages | ArgonEntitlement.ViewChannel, ArgonEntitlement.SendMessages),
                Is.True);
        });
    }

    /// <remarks>
    /// The "chat" mask covers bits 11-19, i.e. ExternalEmoji, ExternalStickers, UseCommands and
    /// PostEmbeddedLinks. Note that it does <em>not</em> cover AttachFiles / AddReactions /
    /// AnyMentions / MentionEveryone (bits 7-10), so those are satisfied without SendMessages —
    /// see <see cref="IsEntitlementSatisfied_AttachFiles_IsOutsideTheChatMask"/>.
    /// </remarks>
    [Test]
    public void IsEntitlementSatisfied_ChatEntitlement_RequiresSendMessages()
    {
        var withoutSend = ArgonEntitlement.ViewChannel | ArgonEntitlement.ExternalEmoji;
        var withSend    = withoutSend | ArgonEntitlement.SendMessages;

        Assert.Multiple(() =>
        {
            Assert.That(EntitlementAnalyzer.IsEntitlementSatisfied(withoutSend, ArgonEntitlement.ExternalEmoji), Is.False);
            Assert.That(EntitlementAnalyzer.IsEntitlementSatisfied(withSend, ArgonEntitlement.ExternalEmoji), Is.True);
        });
    }

    /// <summary>
    /// Pins the current boundary of the chat mask. AttachFiles sits below it, so — unlike
    /// ExternalEmoji — it does not imply SendMessages. Recorded as behaviour rather than as an
    /// endorsement: if the mask is ever meant to include bits 7-10, this is the test that will
    /// fail and say so.
    /// </summary>
    [Test]
    public void IsEntitlementSatisfied_AttachFiles_IsOutsideTheChatMask()
        => Assert.That(
            EntitlementAnalyzer.IsEntitlementSatisfied(
                ArgonEntitlement.ViewChannel | ArgonEntitlement.AttachFiles, ArgonEntitlement.AttachFiles),
            Is.True);

    [Test]
    public void IsEntitlementSatisfied_JoinToVoice_RequiresViewChannel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                EntitlementAnalyzer.IsEntitlementSatisfied(ArgonEntitlement.JoinToVoice, ArgonEntitlement.JoinToVoice),
                Is.False);

            Assert.That(
                EntitlementAnalyzer.IsEntitlementSatisfied(
                    ArgonEntitlement.JoinToVoice | ArgonEntitlement.ViewChannel, ArgonEntitlement.JoinToVoice),
                Is.True);
        });
    }

    [Test]
    public void IsEntitlementSatisfied_VoiceEntitlement_RequiresJoinToVoice()
    {
        var withoutJoin = ArgonEntitlement.ViewChannel | ArgonEntitlement.Speak;
        var withJoin    = withoutJoin | ArgonEntitlement.JoinToVoice;

        Assert.Multiple(() =>
        {
            Assert.That(EntitlementAnalyzer.IsEntitlementSatisfied(withoutJoin, ArgonEntitlement.Speak), Is.False);
            Assert.That(EntitlementAnalyzer.IsEntitlementSatisfied(withJoin, ArgonEntitlement.Speak), Is.True);
        });
    }

    // ── HasEntitlement: overwrite layering ──────────────────────────────────────────────────────

    [Test]
    public void HasEntitlement_ArchetypeOverwrite_CanDenyAnInheritedRight()
    {
        var role    = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);
        var member  = Member(role);
        var channel = Channel(ArchetypeOverwrite(role.Id, deny: ArgonEntitlement.SendMessages));

        Assert.That(
            EntitlementAnalyzer.HasEntitlement(member, channel, ArgonEntitlement.SendMessages),
            Is.False);
    }

    [Test]
    public void HasEntitlement_ArchetypeOverwrite_CanGrantAMissingRight()
    {
        var role    = Archetype(ArgonEntitlement.ViewChannel);
        var member  = Member(role);
        var channel = Channel(ArchetypeOverwrite(role.Id, allow: ArgonEntitlement.SendMessages));

        Assert.That(
            EntitlementAnalyzer.HasEntitlement(member, channel, ArgonEntitlement.SendMessages),
            Is.True);
    }

    [Test]
    public void HasEntitlement_OverwriteForAnUnrelatedArchetype_IsIgnored()
    {
        var role    = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);
        var member  = Member(role);
        var channel = Channel(ArchetypeOverwrite(Guid.NewGuid(), deny: ArgonEntitlement.SendMessages));

        Assert.That(
            EntitlementAnalyzer.HasEntitlement(member, channel, ArgonEntitlement.SendMessages),
            Is.True);
    }

    [Test]
    public void HasEntitlement_MemberOverwrite_WinsOverArchetypeOverwrite()
    {
        // Member scope is applied after archetype scope, so the narrower rule is the one that
        // survives — the property the permission UI relies on when a moderator un-mutes one person
        // in a channel their whole role is denied in.
        var role   = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);
        var member = Member(role);

        var channel = Channel(
            ArchetypeOverwrite(role.Id, deny: ArgonEntitlement.SendMessages),
            MemberOverwrite(member.Id, allow: ArgonEntitlement.SendMessages));

        Assert.That(
            EntitlementAnalyzer.HasEntitlement(member, channel, ArgonEntitlement.SendMessages),
            Is.True);
    }

    [Test]
    public void HasEntitlement_MemberOverwriteForSomeoneElse_IsIgnored()
    {
        var role    = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);
        var member  = Member(role);
        var channel = Channel(MemberOverwrite(Guid.NewGuid(), deny: ArgonEntitlement.SendMessages));

        Assert.That(
            EntitlementAnalyzer.HasEntitlement(member, channel, ArgonEntitlement.SendMessages),
            Is.True);
    }

    // ── HasAccessTo ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void HasAccessTo_Administrator_BypassesChannelOverwrites()
    {
        var admin   = Archetype(ArgonEntitlementKit.Administrator);
        var member  = Member(admin);
        var channel = Channel(ArchetypeOverwrite(admin.Id, deny: ArgonEntitlement.ViewChannel));

        Assert.That(EntitlementEvaluator.HasAccessTo(member, channel, ArgonEntitlement.ViewChannel), Is.True);
    }

    [Test]
    public void HasAccessTo_ChannelRestrictedToOtherRoles_ExcludesEveryoneElse()
    {
        // A channel that carries an explicit role overwrite for the entitlement being checked is
        // treated as opt-in: members who hold none of the named roles are excluded even if their
        // base permissions would otherwise allow it.
        var everyone = Archetype(ArgonEntitlement.ViewChannel, name: "everyone");
        var member   = Member(everyone);
        var channel  = Channel(ArchetypeOverwrite(Guid.NewGuid(), allow: ArgonEntitlement.ViewChannel));

        Assert.That(EntitlementEvaluator.HasAccessTo(member, channel, ArgonEntitlement.ViewChannel), Is.False);
    }

    [Test]
    public void HasAccessTo_ChannelRestrictedToTheMembersRole_Admits()
    {
        var role    = Archetype(ArgonEntitlement.ViewChannel);
        var member  = Member(role);
        var channel = Channel(ArchetypeOverwrite(role.Id, allow: ArgonEntitlement.ViewChannel));

        Assert.That(EntitlementEvaluator.HasAccessTo(member, channel, ArgonEntitlement.ViewChannel), Is.True);
    }

    [Test]
    public void HasAccessTo_ChannelWithoutRelevantOverwrites_FallsBackToBasePermissions()
    {
        var role    = Archetype(ArgonEntitlement.ViewChannel);
        var member  = Member(role);
        var channel = Channel();

        Assert.Multiple(() =>
        {
            Assert.That(EntitlementEvaluator.HasAccessTo(member, channel, ArgonEntitlement.ViewChannel), Is.True);
            Assert.That(EntitlementEvaluator.HasAccessTo(member, channel, ArgonEntitlement.ManageChannels), Is.False);
        });
    }

    // ── CalculatePermissions ────────────────────────────────────────────────────────────────────

    [Test]
    public void CalculatePermissions_ForAForeignSpace_IsNone()
    {
        var member = Member(Archetype(ArgonEntitlementKit.Administrator));

        Assert.That(
            EntitlementEvaluator.CalculatePermissions(member, Guid.NewGuid()),
            Is.EqualTo(ArgonEntitlement.None));
    }

    [Test]
    public void CalculatePermissions_ForAnAdministrator_CollapsesToTheAdministratorKit()
    {
        var member = Member(Archetype(ArgonEntitlementKit.Administrator), Archetype(ArgonEntitlement.ViewChannel));

        Assert.That(
            EntitlementEvaluator.CalculatePermissions(member, SpaceId),
            Is.EqualTo(ArgonEntitlementKit.Administrator));
    }

    [Test]
    public void CalculatePermissions_ForASpace_UnionsArchetypes()
    {
        var member = Member(
            Archetype(ArgonEntitlement.ViewChannel),
            Archetype(ArgonEntitlement.SendMessages));

        Assert.That(
            EntitlementEvaluator.CalculatePermissions(member, SpaceId),
            Is.EqualTo(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages));
    }

    [Test]
    public void CalculatePermissions_ForAChannel_AppliesOverwrites()
    {
        var role   = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);
        var member = Member(role);

        var channel = Channel(ArchetypeOverwrite(role.Id, deny: ArgonEntitlement.SendMessages));

        var permissions = EntitlementEvaluator.CalculatePermissions(member, channel);

        Assert.Multiple(() =>
        {
            Assert.That(permissions.HasFlag(ArgonEntitlement.ViewChannel), Is.True);
            Assert.That(permissions.HasFlag(ArgonEntitlement.SendMessages), Is.False);
        });
    }

    [Test]
    public void CalculatePermissions_ForAChannel_AdministratorIgnoresOverwrites()
    {
        var admin  = Archetype(ArgonEntitlementKit.Administrator);
        var member = Member(admin);

        var channel = Channel(ArchetypeOverwrite(admin.Id, deny: ArgonEntitlement.ViewChannel));

        Assert.That(
            EntitlementEvaluator.CalculatePermissions(member, channel),
            Is.EqualTo(ArgonEntitlementKit.Administrator));
    }

    // ── ApplyPermissionOverwrites ordering ──────────────────────────────────────────────────────

    [Test]
    public void ApplyPermissionOverwrites_MemberScopeIsAppliedLast()
    {
        var role   = Archetype(ArgonEntitlement.ViewChannel);
        var member = Member(role);

        var channel = Channel(
            ArchetypeOverwrite(role.Id, allow: ArgonEntitlement.SendMessages),
            MemberOverwrite(member.Id, deny: ArgonEntitlement.SendMessages));

        var permissions = EntitlementEvaluator.ApplyPermissionOverwrites(
            EntitlementEvaluator.GetBasePermissions(member), member, channel);

        Assert.That(permissions.HasFlag(ArgonEntitlement.SendMessages), Is.False);
    }

    [Test]
    public void ApplyPermissionOverwrites_WithNoOverwrites_IsIdentity()
    {
        var member  = Member(Archetype(ArgonEntitlement.ViewChannel));
        var channel = Channel();
        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);

        Assert.That(
            EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, channel),
            Is.EqualTo(basePermissions));
    }

    // ── IsAllowedToEdit ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void IsAllowedToEdit_Administrator_MayEditAnyArchetype()
    {
        var admin  = Archetype(ArgonEntitlementKit.Administrator);
        var target = Archetype(ArgonEntitlementKit.Administrator);

        Assert.That(EntitlementEvaluator.IsAllowedToEdit(target, [admin]), Is.True);
    }

    [Test]
    public void IsAllowedToEdit_RequiresHoldingEveryRightTheTargetHas()
    {
        var editor = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);

        Assert.Multiple(() =>
        {
            Assert.That(
                EntitlementEvaluator.IsAllowedToEdit(Archetype(ArgonEntitlement.ViewChannel), [editor]),
                Is.True, "a subset of the editor's own rights is editable");

            Assert.That(
                EntitlementEvaluator.IsAllowedToEdit(Archetype(ArgonEntitlement.ManageServer), [editor]),
                Is.False, "an archetype holding rights the editor lacks is not editable");
        });
    }

    [Test]
    public void IsAllowedToEdit_WithPromptedEntitlement_BlocksPrivilegeEscalation()
    {
        var editor = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);
        var target = Archetype(ArgonEntitlement.ViewChannel);

        Assert.Multiple(() =>
        {
            Assert.That(
                EntitlementEvaluator.IsAllowedToEdit(target, ArgonEntitlement.SendMessages, [editor]),
                Is.True, "granting a right the editor already holds is allowed");

            Assert.That(
                EntitlementEvaluator.IsAllowedToEdit(target, ArgonEntitlement.ManageServer, [editor]),
                Is.False, "granting a right the editor does not hold is escalation");
        });
    }

    [Test]
    public void IsAllowedToEdit_WithPromptedEntitlement_ManageServerActsAsAWildcard()
    {
        var editor = Archetype(ArgonEntitlement.ManageServer);
        var target = Archetype(ArgonEntitlement.ViewChannel);

        Assert.That(
            EntitlementEvaluator.IsAllowedToEdit(target, ArgonEntitlement.KickMember, [editor]),
            Is.True);
    }

    [Test]
    public void IsAllowedToEdit_WithPromptedEntitlement_RefusesToEditYourOwnArchetype()
    {
        // Editing the archetype you derive your own authority from is how a member would ratchet
        // themselves upwards one grant at a time; it is refused outright below ManageServer.
        var editor = Archetype(ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);

        Assert.That(
            EntitlementEvaluator.IsAllowedToEdit(editor, ArgonEntitlement.ViewChannel, [editor]),
            Is.False);
    }
}
