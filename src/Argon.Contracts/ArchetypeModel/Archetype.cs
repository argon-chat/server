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
    public Guid ServerId { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(512)]
    public string Description { get; set; } = string.Empty;

    public ArgonEntitlement Entitlement { get; set; }

    [TsIgnore]
    public bool IsMentionable { get; set; }
    [TsIgnore]
    public bool IsLocked { get; set; }
    [TsIgnore]
    public bool IsHidden { get; set; }


    public Color Colour { get; set; }
    [MaxLength(128)]
    public string? IconFileId { get; set; } = null;

    public virtual ICollection<ServerMemberArchetype> ServerMemberRoles { get; set; }
        = new List<ServerMemberArchetype>();
}

[MessagePackObject(true)]
public record ArchetypeDto
{
    public Guid   Id          { get; set; }
    public Guid   ServerId    { get; set; }
    public string Name        { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IsMentionable { get; set; }
    public int  Colour        { get; set; }
    public bool IsHidden      { get; set; }
    public bool IsLocked      { get; set; }

    public string? IconFileId { get; set; } = null;

    public ArgonEntitlement Entitlement { get; set; }
}

public static class ArchetypeExtensions
{
    public static ArchetypeDto ToDto(this Archetype msg)
        => new()
        {
            Colour        = msg.Colour.ToArgb(),
            Description   = msg.Description,
            IsMentionable = msg.IsMentionable,
            Name          = msg.Name,
            ServerId      = msg.ServerId,
            IconFileId    = msg.IconFileId,
            IsHidden      = msg.IsHidden,
            IsLocked      = msg.IsLocked,
            Id            = msg.Id,
            Entitlement   = msg.Entitlement
        };

    public static List<ArchetypeDto> ToDto(this List<Archetype> msg)        => msg.Select(x => x.ToDto()).ToList();
    public static List<ArchetypeDto> ToDto(this ICollection<Archetype> msg) => msg.Select(x => x.ToDto()).ToList();

    public async static Task<ArchetypeDto>       ToDto(this Task<Archetype> msg)       => (await msg).ToDto();
    public async static Task<List<ArchetypeDto>> ToDto(this Task<List<Archetype>> msg) => (await msg).ToDto();
}