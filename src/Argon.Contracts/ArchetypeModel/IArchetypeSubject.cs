namespace Argon.ArchetypeModel;

[TsInterface]
// subject with assigned archetype
public interface IArchetypeSubject
{
    ICollection<IArchetype> SubjectArchetypes { get; }
}