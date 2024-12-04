namespace Argon;

using ArchetypeModel;
using Streaming;
using Servers;

[TsInterface, MessagePackObject(true)]
public record Channel : ArgonEntityWithOwnership, IArchetypeObject
{
    public ChannelType ChannelType { get; set; }
    public Guid        ServerId    { get; set; }
    [IgnoreMember, JsonIgnore, TsIgnore]
    public virtual Server Server { get; set; }


    [MaxLength(128)]
    public required string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Description { get; set; } = null;

    public virtual ICollection<ChannelEntitlementOverwrite> EntitlementOverwrites { get; set; }
        = new List<ChannelEntitlementOverwrite>();
    public ICollection<IArchetypeOverwrite> Overwrites => EntitlementOverwrites.OfType<IArchetypeOverwrite>().ToList();
}

[TsInterface, MessagePackObject(true)]
public record RealtimeChannel
{
    public Channel Channel { get; set; }

    public List<RealtimeChannelUser> Users { get; set; }
}

[TsInterface, MessagePackObject(true)]
public record RealtimeChannelUser
{
    public Guid UserId { get; set; }

    public ChannelMemberState State { get; set; }
}

[Flags]
public enum ChannelMemberState
{
    NONE                       = 0,
    MUTED                      = 1 << 1,
    MUTED_BY_SERVER            = 1 << 2,
    MUTED_HEADPHONES           = 1 << 3,
    MUTED_HEADPHONES_BY_SERVER = 1 << 4,
    STREAMING                  = 1 << 5
}