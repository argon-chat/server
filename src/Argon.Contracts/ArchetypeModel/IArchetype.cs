namespace Argon.ArchetypeModel;

[TsInterface]
public interface IArchetype
{
    Guid   Id   { get; }
    string Name { get; }

    ArgonEntitlement Entitlement { get; }
}