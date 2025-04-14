namespace Argon;

using ArchetypeModel;
using Features.Web;
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

[MessagePackObject(true), TsInterface]
public record ServerMemberDto(Guid UserId, Guid ServerId, long JoinedAt, Guid MemberId, UserDto? User);

public static class ServerMemberExtensions
{
    public static ServerMemberDto ToDto(this ServerMember msg) => new(msg.UserId, msg.ServerId, msg.JoinedAt.ToUnixTimestamp(), msg.Id, msg.User?.ToDto());

    public static List<ServerMemberDto> ToDto(this List<ServerMember> msg) => msg.Select(x => x.ToDto()).ToList();

    public async static Task<ServerMemberDto>       ToDto(this Task<ServerMember> msg)       => (await msg).ToDto();
    public async static Task<List<ServerMemberDto>> ToDto(this Task<List<ServerMember>> msg) => (await msg).ToDto();
}

[TsInterface, MessagePackObject(true)]
public record RealtimeServerMember
{
    public ServerMemberDto Member { get; set; }
    public UserStatus   Status { get; set; }
}