namespace Argon.Contracts.Models.ArchetypeModel;

using Reinforced.Typings.Attributes;

[TsInterface]
public interface IArchetypeOverwrite
{
    Guid             ChannelId { get; }
    IArchetypeScope  Scope     { get; }
    ArgonEntitlement Allow     { get; }
    ArgonEntitlement Deny      { get; }
}