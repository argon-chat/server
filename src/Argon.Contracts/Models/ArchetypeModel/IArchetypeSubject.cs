namespace Argon.Contracts.Models.ArchetypeModel;

using Reinforced.Typings.Attributes;

[TsInterface]
// subject with assigned archetype
public interface IArchetypeSubject
{
    ICollection<IArchetype> SubjectArchetypes { get; }
}