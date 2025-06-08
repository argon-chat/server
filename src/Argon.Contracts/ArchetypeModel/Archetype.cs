namespace Argon.ArchetypeModel;

using System.Drawing;

[MessagePackObject(true), TsInterface]
public record Archetype : ArgonEntityWithOwnership, IArchetype
{
    public static readonly Guid DefaultArchetype_Everyone
        = Guid.Parse("11111111-3333-0000-1111-111111111111");
    public static readonly Guid DefaultArchetype_Owner
        = Guid.Parse("11111111-4444-0000-1111-111111111111");

    [IgnoreMember, TsIgnore]
    public virtual Server Server { get; set; }
    [TsIgnore]
    public Guid ServerId { get;         set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(512)]
    public string Description { get; set; } = string.Empty;

    public ArgonEntitlement Entitlement { get; set; }

    [TsIgnore]
    public bool IsMentionable { get; set; }
    [TsIgnore]
    public bool IsLocked      { get; set; }
    [TsIgnore]
    public bool IsHidden      { get; set; }

    
    public Color Colour { get; set; }
    [MaxLength(128)]
    public string? IconFileId { get; set; } = null;

    public virtual ICollection<ServerMemberArchetype> ServerMemberRoles { get; set; }
        = new List<ServerMemberArchetype>();
}

public record ArchetypeDto
{
    public Guid   ServerId    { get; set; }
    public string Name        { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IsMentionable { get; set; }
    public int  Colour        { get; set; }
}