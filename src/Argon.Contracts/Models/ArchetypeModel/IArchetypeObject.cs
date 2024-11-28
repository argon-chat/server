namespace Argon.Contracts.Models.ArchetypeModel;

using Reinforced.Typings.Attributes;

[TsInterface]
public interface IArchetypeObject
{
    ICollection<IArchetypeOverwrite> Overwrites { get; }
}