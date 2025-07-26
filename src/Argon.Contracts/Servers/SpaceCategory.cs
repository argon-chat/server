namespace Argon.Shared.Servers;

using ArchetypeModel;

[TsInterface, MessagePackObject(true)]
public record SpaceCategory : OrderableArgonEntity, IArchetypeObject
{
    public Guid ServerId { get; set; }
    [IgnoreMember, JsonIgnore, TsIgnore]
    public virtual Server Server { get; set; }

    [IgnoreMember, JsonIgnore, TsIgnore]
    public virtual ICollection<Channel> Channels { get; set; }

    [MaxLength(64)]
    public string Title { get; set; }

    public virtual ICollection<ChannelEntitlementOverwrite> EntitlementOverwrites { get; set; }
        = new List<ChannelEntitlementOverwrite>();
    public ICollection<IArchetypeOverwrite> Overwrites      => EntitlementOverwrites.OfType<IArchetypeOverwrite>().ToList();
}


[MessagePackObject(true)]
public sealed record SpaceCategoryDto(Guid CategoryId, Guid ServerId, string FractionIndex, string Title);

public static class SpaceCategoryExtensions
{
    public static SpaceCategoryDto ToDto(this SpaceCategory msg) => new(msg.Id, msg.ServerId, msg.FractionalIndex, msg.Title);

    public static List<SpaceCategoryDto> ToDto(this List<SpaceCategory> msg) => msg.Select(x => x.ToDto()).ToList();

    public async static Task<SpaceCategoryDto>       ToDto(this Task<SpaceCategory> msg)       => (await msg).ToDto();
    public async static Task<List<SpaceCategoryDto>> ToDto(this Task<List<SpaceCategory>> msg) => (await msg).ToDto();
}