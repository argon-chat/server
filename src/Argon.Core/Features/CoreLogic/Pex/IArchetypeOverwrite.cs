namespace Argon.ArchetypeModel;

public interface IArchetypeOverwrite
{
    IArchetypeScope  Scope          { get; }
    ArgonEntitlement Allow          { get; }
    ArgonEntitlement Deny           { get; }
    Guid?            SpaceMemberId { get; }
    Guid?            ArchetypeId    { get; }
}