namespace Argon.Contracts.Models;

using ArchetypeModel;
using MessagePack;
using Reinforced.Typings.Attributes;

[MessagePackObject(true), TsInterface]
public record ChannelEntitlementOverwrite : ArgonEntityWithOwnership, IArchetypeOverwrite
{
    public         Guid    ChannelId { get; set; }
    [IgnoreMember, TsIgnore]
    public virtual Channel Channel   { get; set; }

    public IArchetypeScope Scope { get; set; }

    public         Guid?     ArchetypeId { get; set; }
    [IgnoreMember, TsIgnore]
    public virtual Archetype Archetype   { get; set; }

    public         Guid?        ServerMemberId { get; set; }
    [IgnoreMember, TsIgnore]
    public virtual ServerMember ServerMember   { get; set; }

    public ArgonEntitlement Allow { get; set; }
    public ArgonEntitlement Deny  { get; set; }
}