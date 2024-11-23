namespace Argon.Contracts.Models.ArchetypeModel;

public interface IArchetypeOverwrite
{
    Guid             ChannelId { get; }
    IArchetypeScope  Scope     { get; }
    ArgonEntitlement Allow     { get; }
    ArgonEntitlement Deny      { get; }
}