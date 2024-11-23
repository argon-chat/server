namespace Argon.Contracts.Models.ArchetypeModel;

using System.ComponentModel.DataAnnotations;
using System.Drawing;
using MessagePack;

[MessagePackObject(true)]
public record Archetype : ArgonEntityWithOwnership, IArchetype
{
    public static readonly Guid DefaultArchetype_Everyone
        = Guid.Parse("11111111-3333-0000-1111-111111111111");
    public static readonly Guid DefaultArchetype_Owner
        = Guid.Parse("11111111-4444-0000-1111-111111111111");

    [IgnoreMember]
    public virtual Server Server { get; set; }
    public Guid ServerId { get;         set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(512)]
    public string Description { get; set; } = string.Empty;

    public ArgonEntitlement Entitlement { get; set; }

    public bool IsMentionable { get; set; }
    public bool IsLocked      { get; set; }
    public bool IsHidden      { get; set; }

    public Color Colour { get; set; }
    [MaxLength(128)]
    public string? IconFileId { get; set; } = null;

    public virtual ICollection<ServerMemberArchetype> ServerMemberRoles { get; set; }
        = new List<ServerMemberArchetype>();
}

public interface IArchetype
{
    Guid   Id   { get; }
    string Name { get; }

    ArgonEntitlement Entitlement { get; }
}