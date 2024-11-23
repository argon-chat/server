namespace Argon.Contracts.Models.ArchetypeModel;

public interface IArchetypeObject
{
    ICollection<IArchetypeOverwrite> Overwrites { get; }
}