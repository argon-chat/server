namespace Argon.Contracts.Models;

using MessagePack;
using Reinforced.Typings.Attributes;

[TsInterface]
public record ServerMember : ArgonEntityWithOwnership
{
    public Guid ServerId { get; set; }
    public Guid UserId   { get; set; }

    public virtual User   User   { get; set; }
    [IgnoreMember, TsIgnore]
    public virtual Server Server { get; set; }

    public DateTime JoinedAt { get; set; }

    public ICollection<ServerMemberArchetype> ServerMemberArchetypes { get; set; }
        = new List<ServerMemberArchetype>();
}