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
    public virtual ICollection<ServerInvite> ServerInvites     { get; set; } = new List<ServerInvite>();
}

[MessagePackObject(true)]
public record ServerInvite : ArgonEntityWithOwnership<ulong>
{
    public         DateTime Expired  { get; set; }
    public         Guid     ServerId { get; set; }
    public virtual Server   Server   { get; set; }
}