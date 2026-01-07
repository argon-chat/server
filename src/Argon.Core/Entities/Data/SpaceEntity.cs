namespace Argon.Entities;

using ion.runtime;

public record SpaceEntity : ArgonEntityWithOwnership, IArchetypeSubject, IMapper<SpaceEntity, ArgonSpace>
{
    public static readonly Guid DefaultSystemSpace
        = Guid.Parse("11111111-0000-1111-1111-111111111111");

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Description { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? AvatarFileId { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? TopBannedFileId { get; set; }
    
    public bool IsCommunity { get; set; }
    public Guid? DefaultChannelId { get; set; }

    public virtual ICollection<ChannelEntity>      Channels       { get; set; } = new List<ChannelEntity>();
    public virtual ICollection<ChannelGroupEntity> ChannelGroups  { get; set; } = new List<ChannelGroupEntity>();
    public virtual ICollection<SpaceMemberEntity>  Users          { get; set; } = new List<SpaceMemberEntity>();
    public virtual ICollection<ArchetypeEntity>    Archetypes     { get; set; } = new List<ArchetypeEntity>();
    public         ICollection<IArchetype>         SubjectArchetypes => Archetypes.OfType<IArchetype>().ToList();
    public virtual ICollection<SpaceInvite>        ServerInvites  { get; set; } = new List<SpaceInvite>();

    public static ArgonSpace Map(scoped in SpaceEntity self)
        => new(self.Id, self.Name, self.Description ?? "", self.AvatarFileId, self.TopBannedFileId,
            IonArray<ArgonChannel>.Empty, IonArray<SpaceMember>.Empty, IonArray<Archetype>.Empty);
}