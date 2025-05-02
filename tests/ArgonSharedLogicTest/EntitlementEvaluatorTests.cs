namespace ArgonSharedLogicTest;

using Argon;
using Argon.ArchetypeModel;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Drawing;
using Argon.Servers;
using Argon.Users;

[TestFixture]
public class EntitlementEvaluatorTests
{
    [Test]
    public void TestFlow1()
    {
        var serverId = Guid.NewGuid();


        var everyoneDefaultArch = new Archetype()
        {
            Id          = Guid.NewGuid(),
            ServerId    = serverId,
            Colour      = Color.AliceBlue,
            Entitlement = ArgonEntitlement.BaseMember,
            IsLocked    = true
        };
        var adminDefaultArch = new Archetype()
        {
            Id          = Guid.NewGuid(),
            ServerId    = serverId,
            Colour      = Color.Red,
            IsLocked    = true,
            Entitlement = ArgonEntitlement.Administrator
        };
        var moderatorDefaultArch = new Archetype()
        {
            Id          = Guid.NewGuid(),
            ServerId    = serverId,
            Colour      = Color.Orange,
            IsLocked    = true,
            Entitlement = ArgonEntitlement.ModerateMembers
        };


        var channelId1 = new Channel()
        {
            Id          = Guid.NewGuid(),
            ChannelType = ChannelType.Voice,
            ServerId    = serverId
        };
        var channelId2 = new Channel()
        {
            Id          = Guid.NewGuid(),
            ChannelType = ChannelType.Voice,
            ServerId    = serverId,
            EntitlementOverwrites = new List<ChannelEntitlementOverwrite>()
            {
                new()
                {
                    Id          = Guid.NewGuid(),
                    Archetype   = moderatorDefaultArch,
                    ArchetypeId = moderatorDefaultArch.Id,
                    Scope       = IArchetypeScope.Archetype,
                    Allow       = ArgonEntitlement.ViewChannel
                }
            }
        };
        var channelId3 = new Channel()
        {
            Id          = Guid.NewGuid(),
            ChannelType = ChannelType.Voice,
            ServerId    = serverId,
            EntitlementOverwrites = new List<ChannelEntitlementOverwrite>()
            {
                new()
                {
                    Id          = Guid.NewGuid(),
                    Archetype   = everyoneDefaultArch,
                    ArchetypeId = everyoneDefaultArch.Id,
                    Scope       = IArchetypeScope.Archetype,
                    Deny        = ArgonEntitlement.ViewChannel
                }
            }
        };


        var user1 = new ServerMember()
        {
            User = new User()
            {
                DisplayName = "",
                Email       = "",
                Username    = "",
                Id          = Guid.NewGuid()
            },
            Id       = Guid.NewGuid(),
            ServerId = serverId,
            ServerMemberArchetypes = new List<ServerMemberArchetype>()
            {
                new()
                {
                    Archetype   = everyoneDefaultArch,
                    ArchetypeId = everyoneDefaultArch.Id,
                }
            }
        };

        var user2 = new ServerMember()
        {
            User = new User()
            {
                DisplayName = "",
                Email       = "",
                Username    = "",
                Id          = Guid.NewGuid()
            },
            Id       = Guid.NewGuid(),
            ServerId = serverId,
            ServerMemberArchetypes = new List<ServerMemberArchetype>()
            {
                new()
                {
                    Archetype   = everyoneDefaultArch,
                    ArchetypeId = everyoneDefaultArch.Id,
                },
                new()
                {
                    Archetype   = moderatorDefaultArch,
                    ArchetypeId = moderatorDefaultArch.Id,
                }
            }
        };

        var user3 = new ServerMember()
        {
            User = new User()
            {
                DisplayName = "",
                Email       = "",
                Username    = "",
                Id          = Guid.NewGuid()
            },
            Id       = Guid.NewGuid(),
            ServerId = serverId,
            ServerMemberArchetypes = new List<ServerMemberArchetype>()
            {
                new()
                {
                    Archetype   = everyoneDefaultArch,
                    ArchetypeId = everyoneDefaultArch.Id,
                },
                new()
                {
                    Archetype   = adminDefaultArch,
                    ArchetypeId = adminDefaultArch.Id,
                }
            }
        };

        var server = new Server()
        {
            Id = serverId,
            Channels = new List<Channel>()
            {
                channelId1,
                channelId2,
                channelId3
            },
            Archetypes = new List<Archetype>()
            {
                everyoneDefaultArch,
                moderatorDefaultArch,
                adminDefaultArch
            },
            Users = new List<ServerMember>()
            {
                user1,
                user2,
                user3
            }
        };


        var r1 = EntitlementEvaluator.HasAccessTo(user1, channelId1, ArgonEntitlement.ViewChannel);
        var r2 = EntitlementEvaluator.HasAccessTo(user1, channelId2, ArgonEntitlement.ViewChannel);
        var r3 = EntitlementEvaluator.HasAccessTo(user1, channelId3, ArgonEntitlement.ViewChannel);


        var r4 = EntitlementEvaluator.HasAccessTo(user2, channelId1, ArgonEntitlement.ViewChannel);
        var r5 = EntitlementEvaluator.HasAccessTo(user2, channelId2, ArgonEntitlement.ViewChannel);
        var r6 = EntitlementEvaluator.HasAccessTo(user2, channelId3, ArgonEntitlement.ViewChannel);


        var r7 = EntitlementEvaluator.HasAccessTo(user3, channelId1, ArgonEntitlement.ViewChannel);
        var r8 = EntitlementEvaluator.HasAccessTo(user3, channelId2, ArgonEntitlement.ViewChannel);
        var r9 = EntitlementEvaluator.HasAccessTo(user3, channelId3, ArgonEntitlement.ViewChannel);
    }


    [Test]
    public void CalculatePermissions_ServerIdMismatch_ReturnsNone()
    {
        var member = new ServerMember
        {
            ServerId = Guid.NewGuid()
        };

        var result = EntitlementEvaluator.CalculatePermissions(member, Guid.NewGuid());

        Assert.That(result, Is.EqualTo(ArgonEntitlement.None));
    }

    [Test]
    public void CalculatePermissions_ServerLevel_Admin_ReturnsAdministrator()
    {
        var member = new ServerMember
        {
            ServerId = Guid.NewGuid(),
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.Administrator
                    }
                }
            }
        };

        var result = EntitlementEvaluator.CalculatePermissions(member, member.ServerId);

        Assert.That(result, Is.EqualTo(ArgonEntitlement.Administrator));
    }

    [Test]
    public void CalculatePermissions_ServerLevel_MultipleRoles_ReturnsCombinedEntitlements()
    {
        var member = new ServerMember
        {
            ServerId = Guid.NewGuid(),
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory
                    }
                },
                new()
                {
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.JoinToVoice
                    }
                }
            }
        };

        var result = EntitlementEvaluator.CalculatePermissions(member, member.ServerId);

        Assert.That(result, Is.EqualTo(ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory | ArgonEntitlement.JoinToVoice));
    }

    [Test]
    public void CalculatePermissions_ChannelLevel_Admin_ReturnsAdministrator()
    {
        var member = new ServerMember
        {
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.Administrator
                    }
                }
            }
        };

        var channel = new Channel();

        var result = EntitlementEvaluator.CalculatePermissions(member, channel);

        Assert.That(result, Is.EqualTo(ArgonEntitlement.Administrator));
    }

    [Test]
    public void ApplyPermissionOverwrites_CorrectlyAppliesRoleAndMemberOverwrites()
    {
        var archetypeId = Guid.NewGuid();
        var memberId    = Guid.NewGuid();

        var member = new ServerMember
        {
            Id = memberId,
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    ArchetypeId = archetypeId,
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages
                    }
                }
            }
        };

        var channel = new Channel
        {
            EntitlementOverwrites = new List<ChannelEntitlementOverwrite>
            {
                new()
                {
                    Scope       = IArchetypeScope.Archetype,
                    ArchetypeId = archetypeId,
                    Allow       = ArgonEntitlement.PostEmbeddedLinks,
                    Deny        = ArgonEntitlement.SendMessages
                },
                new()
                {
                    Scope          = IArchetypeScope.Member,
                    ServerMemberId = memberId,
                    Allow          = ArgonEntitlement.JoinToVoice,
                    Deny           = ArgonEntitlement.ViewChannel
                }
            }
        };

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);
        var result          = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, channel);

        var expected = ArgonEntitlement.JoinToVoice | ArgonEntitlement.PostEmbeddedLinks;

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void GetBasePermissions_NoArchetypes_ReturnsNone()
    {
        var member = new ServerMember
        {
            ServerMemberArchetypes = new List<ServerMemberArchetype>()
        };

        var result = EntitlementEvaluator.GetBasePermissions(member);

        Assert.That(result, Is.EqualTo(ArgonEntitlement.None));
    }

    [Test]
    public void ApplyPermissionOverwrites_NoOverwrites_ReturnsBasePermissions()
    {
        var member = new ServerMember
        {
            Id = Guid.NewGuid(),
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.BaseChat
                    }
                }
            }
        };

        var channel = new Channel
        {
            EntitlementOverwrites = new List<ChannelEntitlementOverwrite>() // пусто
        };

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);
        var result          = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, channel);

        Assert.That(result, Is.EqualTo(basePermissions));
    }

    [Test]
    public void ApplyPermissionOverwrites_OnlyMemberOverwrite_AppliesCorrectly()
    {
        var memberId = Guid.NewGuid();

        var member = new ServerMember
        {
            Id = memberId,
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.ReadHistory | ArgonEntitlement.SendMessages
                    }
                }
            }
        };

        var channel = new Channel
        {
            EntitlementOverwrites = new List<ChannelEntitlementOverwrite>
            {
                new()
                {
                    Scope          = IArchetypeScope.Member,
                    ServerMemberId = memberId,
                    Allow          = ArgonEntitlement.MentionEveryone,
                    Deny           = ArgonEntitlement.SendMessages
                }
            }
        };

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);
        var result          = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, channel);

        var expected = (basePermissions & ~ArgonEntitlement.SendMessages) | ArgonEntitlement.MentionEveryone;

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ApplyPermissionOverwrites_OnlyRoleOverwrite_AppliesCorrectly()
    {
        var archetypeId = Guid.NewGuid();

        var member = new ServerMember
        {
            Id = Guid.NewGuid(),
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    ArchetypeId = archetypeId,
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.ViewChannel | ArgonEntitlement.UseCommands
                    }
                }
            }
        };

        var channel = new Channel
        {
            EntitlementOverwrites = new List<ChannelEntitlementOverwrite>
            {
                new()
                {
                    Scope       = IArchetypeScope.Archetype,
                    ArchetypeId = archetypeId,
                    Allow       = ArgonEntitlement.AttachFiles,
                    Deny        = ArgonEntitlement.UseCommands
                }
            }
        };

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);
        var result          = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, channel);

        var expected = (basePermissions & ~ArgonEntitlement.UseCommands) | ArgonEntitlement.AttachFiles;

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ApplyPermissionOverwrites_MemberNotInRoleOverwrite_SkipsThatOverwrite()
    {
        var member = new ServerMember
        {
            Id = Guid.NewGuid(),
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    ArchetypeId = Guid.NewGuid(), // не тот, что в overwrite
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.Connect
                    }
                }
            }
        };

        var channel = new Channel
        {
            EntitlementOverwrites = new List<ChannelEntitlementOverwrite>
            {
                new()
                {
                    Scope       = IArchetypeScope.Archetype,
                    ArchetypeId = Guid.NewGuid(), // не совпадает
                    Allow       = ArgonEntitlement.Speak,
                    Deny        = ArgonEntitlement.Connect
                }
            }
        };

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);
        var result          = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, channel);

        Assert.That(result, Is.EqualTo(basePermissions));
    }

    [Test]
    public void ApplyPermissionOverwrites_EmptyMemberAndRoleOverwrites_NoChange()
    {
        var member = new ServerMember
        {
            Id = Guid.NewGuid(),
            ServerMemberArchetypes = new List<ServerMemberArchetype>
            {
                new()
                {
                    Archetype = new Archetype
                    {
                        Entitlement = ArgonEntitlement.BaseExtended
                    }
                }
            }
        };

        var channel = new Channel(); // no overwrites

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);
        var result          = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, channel);

        Assert.That(result, Is.EqualTo(basePermissions));
    }
}