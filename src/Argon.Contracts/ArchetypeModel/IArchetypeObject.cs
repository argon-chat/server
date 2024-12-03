namespace Argon.ArchetypeModel;

[TsInterface]
public interface IArchetypeObject
{
    ICollection<IArchetypeOverwrite> Overwrites { get; }
}