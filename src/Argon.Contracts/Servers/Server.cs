namespace Argon;

using ArchetypeModel;

[TsInterface, MessagePackObject(true)]
public record Server : ArgonEntityWithOwnership, IArchetypeSubject
{
    public static readonly Guid DefaultSystemServer
        = Guid.Parse("11111111-0000-1111-1111-111111111111");

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Description { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? AvatarFileId { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? TopBannedFileId { get; set; }

    public virtual ICollection<Channel>      Channels          { get; set; } = new List<Channel>();
    public virtual ICollection<ServerMember> Users             { get; set; } = new List<ServerMember>();
    public virtual ICollection<Archetype>    Archetypes        { get; set; } = new List<Archetype>();
    public         ICollection<IArchetype>   SubjectArchetypes => Archetypes.OfType<IArchetype>().ToList();
    [TsIgnore]
    public virtual ICollection<ServerInvite> ServerInvites { get; set; } = new List<ServerInvite>();
}

[MessagePackObject(true), TsInterface]
public record ServerDto(
    Guid Id,
    string Name,
    string Description,
    string AvatarFieldId,
    string TopBannerFileId,
    List<Channel> Channels,
    List<ServerMemberDto> Users,
    List<Archetype> Archetypes);

public static class ServerExtensions
{
    public static ServerDto ToDto(this Server msg) => new(msg.Id, msg.Name, msg.Description, msg.AvatarFileId, msg.TopBannedFileId,
        msg.Channels.ToList(), msg.Users.ToList().ToDto(), msg.Archetypes.ToList());

    public static List<ServerDto> ToDto(this List<Server> msg) => msg.Select(x => x.ToDto()).ToList();

    public async static Task<ServerDto>       ToDto(this Task<Server> msg)       => (await msg).ToDto();
    public async static Task<List<ServerDto>> ToDto(this Task<List<Server>> msg) => (await msg).ToDto();
}

[MessagePackObject(true)]
public record ServerInvite : ArgonEntityWithOwnership<ulong>
{
    public         DateTimeOffset Expired  { get; set; }
    public         Guid           ServerId { get; set; }
    public virtual Server         Server   { get; set; }
}