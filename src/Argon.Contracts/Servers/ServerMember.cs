namespace Argon;

using ArchetypeModel;
using Users;

[TsInterface, MessagePackObject(true)]
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
[TsInterface, MessagePackObject(true)]
public record RealtimeServerMember
{
    public ServerMember Member { get; set; }
    public UserStatus   Status { get; set; }
}