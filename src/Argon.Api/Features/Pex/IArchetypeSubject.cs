namespace Argon.ArchetypeModel;


// subject with assigned archetype
public interface IArchetypeSubject
{
    ICollection<IArchetype> SubjectArchetypes { get; }
}