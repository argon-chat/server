namespace Argon.Contracts.Models;

using System.ComponentModel.DataAnnotations;
using ArchetypeModel;
using MessagePack;
using Newtonsoft.Json;

public record Channel : ArgonEntityWithOwnership, IArchetypeObject
{
    public ChannelType ChannelType { get; set; }
    public Guid        ServerId    { get; set; }
    [IgnoreMember, JsonIgnore]
    public virtual Server Server { get; set; }


    [MaxLength(128)]
    public required string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Description { get; set; } = null;

    public virtual ICollection<ChannelEntitlementOverwrite> EntitlementOverwrites { get; set; }
        = new List<ChannelEntitlementOverwrite>();
    public ICollection<IArchetypeOverwrite> Overwrites => EntitlementOverwrites.OfType<IArchetypeOverwrite>().ToList();
}