namespace Argon.ArchetypeModel;

[TsInterface]
public interface IArchetypeOverwrite
{
    Guid             ChannelId { get; }
    IArchetypeScope  Scope     { get; }
    ArgonEntitlement Allow     { get; }
    ArgonEntitlement Deny      { get; }
}