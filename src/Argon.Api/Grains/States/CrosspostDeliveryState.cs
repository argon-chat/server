namespace Argon.Grains.Persistence.States;

/// <summary>How far the delivery of one published post has got. Cleared once it is done.</summary>
[DataContract, Serializable, GenerateSerializer]
public sealed record CrosspostDeliveryState
{
    [DataMember(Order = 0), Id(0)]
    public Guid SourceSpaceId { get; set; }

    [DataMember(Order = 1), Id(1)]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>The last follow handled, in id order; the next page starts after it.</summary>
    [DataMember(Order = 2), Id(2)]
    public Guid? Cursor { get; set; }

    [DataMember(Order = 3), Id(3)]
    public int Delivered { get; set; }

    /// <summary>Follows deleted because their creator no longer passed the access check.</summary>
    [DataMember(Order = 4), Id(4)]
    public int Dropped { get; set; }
}
