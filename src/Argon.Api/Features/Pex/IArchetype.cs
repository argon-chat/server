namespace Argon.ArchetypeModel;

public interface IArchetype
{
    Guid   Id   { get; }
    string Name { get; }

    ArgonEntitlement Entitlement { get; }
}