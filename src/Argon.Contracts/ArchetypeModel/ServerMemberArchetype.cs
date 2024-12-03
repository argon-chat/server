namespace Argon.ArchetypeModel;

using ArchetypeModel;

[MessagePackObject(true), TsInterface]
public record ServerMemberArchetype
{
    public Guid ServerMemberId { get; set; }
    public Guid ArchetypeId { get; set; }

    [IgnoreMember, TsIgnore]
    public virtual Archetype Archetype { get; set; }
    [IgnoreMember, TsIgnore]
    public virtual ServerMember ServerMember { get; set; }
}