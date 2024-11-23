namespace Argon.Contracts.Models;

using ArchetypeModel;
using MessagePack;


[MessagePackObject(true)]
public record ServerMemberArchetype
{
    public Guid ServerMemberId { get; set; }
    public Guid ArchetypeId    { get; set; }

    [IgnoreMember]
    public virtual Archetype    Archetype    { get; set; }
    [IgnoreMember]
    public virtual ServerMember ServerMember { get; set; }
}