namespace Argon.Contracts.Models;

using ArchetypeModel;
using System.ComponentModel.DataAnnotations;
using MessagePack;
using Reinforced.Typings.Attributes;

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


    public virtual ICollection<Channel>      Channels          { get; set; } = new List<Channel>();
    public virtual ICollection<ServerMember> Users             { get; set; } = new List<ServerMember>();
    public virtual ICollection<Archetype>    Archetypes        { get; set; } = new List<Archetype>();
    public         ICollection<IArchetype>   SubjectArchetypes => Archetypes.OfType<IArchetype>().ToList();
}