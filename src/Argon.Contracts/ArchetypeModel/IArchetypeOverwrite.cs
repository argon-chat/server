namespace Argon.ArchetypeModel;

[TsInterface]
public interface IArchetypeOverwrite
{
    IArchetypeScope  Scope          { get; }
    ArgonEntitlement Allow          { get; }
    ArgonEntitlement Deny           { get; }
    Guid?            ServerMemberId { get; }
    Guid?            ArchetypeId    { get; }
}