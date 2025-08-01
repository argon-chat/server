namespace Argon.ArchetypeModel;

[TsInterface]
public interface IArchetypeObject
{
    ICollection<IArchetypeOverwrite> Overwrites { get; }
}

public interface IFractionalOrder
{
    [MaxLength(30)]
    public string FractionalIndex { get; set; }
}