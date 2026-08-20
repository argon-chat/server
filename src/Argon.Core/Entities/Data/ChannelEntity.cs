namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ChannelEntity :
    OrderableArgonEntity,
    IArchetypeObject,
    IEntityTypeConfiguration<ChannelEntity>,
    IMapper<ChannelEntity, ArgonChannel>
{
    public         ChannelType ChannelType { get; set; }
    public         Guid        SpaceId     { get; set; }
    public virtual SpaceEntity Space       { get; set; }

    public         Guid?               ChannelGroupId { get; set; }
    public virtual ChannelGroupEntity? ChannelGroup   { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(1024)]
    public string? Description { get; set; } = null;

    public TimeSpan? SlowMode              { get; set; }
    public bool      DoNotRestrictBoosters { get; set; }

    /// <summary>
    /// Dead. Nothing writes this column any more — the channel's high-water mark lives in
    /// <see cref="Argon.Core.Entities.Data.ChannelLastMessageEntity"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Do not trust the value.</b> It is whatever the last flush wrote before the counter
    /// moved off this row, frozen there ever since, and for a channel created after that it is zero
    /// forever. It reads like a working counter and is not one — which is the entire reason this
    /// comment exists rather than the column simply being dropped.</para>
    ///
    /// <para>Kept because dropping it is a second migration and a second decision, and the two do not
    /// have to be taken together: this change moves the writer and the readers, and leaving the column
    /// in place is what makes it reversible. The rollback is redeploying the previous build, which
    /// finds the column where it left it. Drop it once that stops being wanted, and expect to touch
    /// <see cref="Map"/> and <c>AdminConsoleImpl.GetSpaceCard</c> when you do.</para>
    ///
    /// <para>It is still read in one place, on purpose: <see cref="Map"/> below fills the DTO field
    /// from it. See the note there for why that is harmless.</para>
    /// </remarks>
    public long LastMessageId { get; set; }

    public virtual ICollection<ChannelEntitlementOverwriteEntity> EntitlementOverwrites { get; set; }
        = new List<ChannelEntitlementOverwriteEntity>();
    public ICollection<IArchetypeOverwrite> Overwrites
        => EntitlementOverwrites.OfType<IArchetypeOverwrite>().ToList();

    /// <summary>The cooldowns a channel may carry. Anything else is refused rather than rounded,
    /// so the picker on one client can never render a value another client cannot.</summary>
    public static readonly int[] AllowedSlowModeSeconds = [0, 5, 15, 30, 60, 300];

    /// <summary>
    /// The channel as the wire sees it.
    /// </summary>
    /// <remarks>
    /// <c>lastMessageId</c> comes off the dead column and is therefore not a real answer here — see
    /// <see cref="LastMessageId"/>. It is filled anyway rather than zeroed, because zero is a claim
    /// ("this channel is empty") and a stale number is at least a number, and because every path that
    /// serves this DTO to a client replaces the field before it leaves: <c>SpaceReadGrain</c> from the
    /// side table overlaid with the Redis cell, <c>ChannelGrain.UpdateChannelSettings</c> from the
    /// side table, and channel creation from the fact that a new channel really does have none. If a
    /// fourth path appears, it owes the same replacement — the mapper cannot do it, because it has no
    /// database and no cache to ask.
    /// </remarks>
    public static ArgonChannel Map(scoped in ChannelEntity self)
        => new(self.ChannelType, self.SpaceId, self.Id, self.Name, self.Description, self.ChannelGroupId,
            string.IsNullOrEmpty(self.FractionalIndex) ? null : self.FractionalIndex, self.LastMessageId,
            // A zero interval is never stored — "off" travels as null so the client has one
            // representation of "no cooldown" instead of two.
            self.SlowMode is { } window && window > TimeSpan.Zero ? (int)window.TotalSeconds : null);

    public void Configure(EntityTypeBuilder<ChannelEntity> builder)
    {
        builder.HasOne(c => c.Space)
           .WithMany(s => s.Channels)
           .HasForeignKey(c => c.SpaceId);

        builder.HasOne(c => c.ChannelGroup)
           .WithMany(g => g.Channels)
           .HasForeignKey(c => c.ChannelGroupId)
           .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new
        {
            x.SpaceId,
            x.ChannelGroupId,
            x.FractionalIndex
        });
    }
}