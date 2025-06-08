namespace Argon.ArchetypeModel;

using ArchetypeModel;
using Argon.Users;

[MessagePackObject(true), TsInterface]
public record ServerMemberArchetype
{
    public Guid ServerMemberId { get; set; }
    public Guid ArchetypeId { get; set; }

    [IgnoreMember, TsIgnore]
    public virtual Archetype Archetype { get; set; }
    [IgnoreMember, TsIgnore]
    public virtual ServerMember ServerMember { get; set; }
}

[MessagePackObject(true), TsInterface]
public record ServerMemberArchetypeDto(Guid ServerMemberId, Guid ArchetypeId);

public static class ServerMemberExtensions
{
    public static ServerMemberArchetypeDto ToDto(this ServerMemberArchetype msg)
        => new(msg.ServerMemberId, msg.ArchetypeId);

    public static List<ServerMemberArchetypeDto> ToDto(this List<ServerMemberArchetype> msg) => msg.Select(x => x.ToDto()).ToList();
    public static List<ServerMemberArchetypeDto> ToDto(this ICollection<ServerMemberArchetype> msg) => msg.Select(x => x.ToDto()).ToList();

    public async static Task<ServerMemberArchetypeDto>       ToDto(this Task<ServerMemberArchetype> msg) => (await msg).ToDto();
    public async static Task<List<ServerMemberArchetypeDto>> ToDto(this Task<List<ServerMemberArchetype>> msg) => (await msg).ToDto();
}