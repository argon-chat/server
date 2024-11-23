namespace Argon.Contracts.Models;

using ArchetypeModel;
using MessagePack;

[MessagePackObject(true)]
public record ChannelEntitlementOverwrite : ArgonEntityWithOwnership, IArchetypeOverwrite
{
    public         Guid    ChannelId { get; set; }
    [IgnoreMember]
    public virtual Channel Channel   { get; set; }

    public IArchetypeScope Scope { get; set; }

    public         Guid?     ArchetypeId { get; set; }
    [IgnoreMember]
    public virtual Archetype Archetype   { get; set; }

    public         Guid?        ServerMemberId { get; set; }
    [IgnoreMember]
    public virtual ServerMember ServerMember   { get; set; }

    public ArgonEntitlement Allow { get; set; }
    public ArgonEntitlement Deny  { get; set; }
}