namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A channel's newest message id, on a row of its own.
/// </summary>
/// <remarks>
/// <para>This table exists because the number used to live on <c>Channels</c>, and it never belonged
/// there. That row is a channel's identity — name, description, group, ordering, slow mode — read on
/// every client bootstrap and by every permission-adjacent path, which is precisely the shape that
/// wants replicating to every region. A counter that moves once per message sent is precisely the
/// shape that cannot be: <c>LOCALITY GLOBAL</c> charges a commit-wait of a few hundred milliseconds
/// on every write. One table could have the cheap read or the cheap write, not both, and the
/// placement audit resolved the conflict by demoting the whole table — treating the symptom. The
/// audit also named this fix and it was skipped; see
/// <c>docs/architecture/table-placement-reconciler.md</c> §5b, the <c>Channels</c> row: <i>"Take
/// LastMessageId off this table. It is a hot counter on a cold row."</i></para>
///
/// <para><b>Nothing else may join this table.</b> The whole value of the split is that writing this
/// row touches no column anybody reads in order to render a channel. A last author, a message count,
/// a cached preview — each would be another reason for a cold path to read this hot row, and the
/// next audit would find the same mistake in a new place. If something new needs a per-channel
/// counter, it gets its own table or it goes in the cache; it does not get a column here.</para>
///
/// <para><b>Keyed by the channel, carrying the space, with a foreign key to neither.</b> An FK to
/// <c>Channels</c> would make the most frequent write in the product take a constraint check against
/// the table this type exists to keep cold, and it would buy nothing to pay for it with: channels are
/// soft-deleted, so the parent row never actually goes away and no cascade would ever fire. A row
/// left behind for a channel that no longer exists is harmless — every reader drives from the channel
/// list and looks marks up by id, so an orphan is simply never asked for.</para>
///
/// <para><b><c>SpaceId</c> is copied from the channel rather than joined for.</b> The badge query
/// asks "every mark in these spaces" and the space snapshot asks "every mark in this space", so the
/// index below turns both into one seek against one table. Without the column those reads would have
/// to name the channels — an IN-list built from a query against <c>Channels</c>, which is a plan over
/// both tables and a predicate whose shape changes per caller. The duplication is what keeps the two
/// halves independent, which is the point of having split them. It is safe because a channel does
/// not change space: <c>MoveChannel</c> moves one between groups within its space, and nothing moves
/// one out of it.</para>
///
/// <para>Not an <see cref="ArgonEntity"/>, deliberately. It would bring a surrogate <c>Id</c> the
/// channel id already covers, a <c>CreatorId</c> that means nothing here, and a soft-delete filter
/// over a table with nothing to soft-delete — and the region-tagged id generator would then be
/// minting keys for rows whose key is not theirs to choose. <c>ChannelReadStateEntity</c> and
/// <c>NotificationCounterEntity</c> are the same shape and the same decision.</para>
/// </remarks>
public record ChannelLastMessageEntity : IEntityTypeConfiguration<ChannelLastMessageEntity>
{
    /// <summary>The channel this mark belongs to, and the whole of the key.</summary>
    public required Guid ChannelId { get; set; }

    /// <summary>The space that channel is in, so a badge query can ask by space without a join.</summary>
    public required Guid SpaceId { get; set; }

    /// <summary>
    /// The highest message id the channel has accepted, as far as anything durable knows.
    /// </summary>
    /// <remarks>
    /// Behind the Redis cell by design — the grain writes the cell on every send and this row once
    /// per flush — so a reader that needs the freshest answer takes the larger of the two. It only
    /// ever rises, which is what makes the maximum correct rather than a guess about which source to
    /// trust; the upsert in <c>ChannelGrain.UpdateLastMessageIdAsync</c> keeps that true even when
    /// two activations overlap during a migration.
    /// </remarks>
    public long LastMessageId { get; set; }

    /// <summary>
    /// When the mark was last moved.
    /// </summary>
    /// <remarks>
    /// Nothing reads it, and it is here anyway: it is the only way to tell a channel that has been
    /// quiet for a week from one whose flush has been failing for a week, and both look identical in
    /// every other column. Cheap on a row this narrow, and impossible to reconstruct later.
    /// </remarks>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Configure(EntityTypeBuilder<ChannelLastMessageEntity> builder)
    {
        builder.ToTable("ChannelLastMessages");

        builder.HasKey(x => x.ChannelId);

        // The channel id comes from the channel. Left to convention, EF treats a lone Guid key as
        // generated-on-add and hands out a fresh one for any row inserted with an empty key — which
        // for this table would silently mint a mark for a channel that does not exist rather than
        // failing. Nothing inserts through the change tracker today (the writer upserts in SQL), so
        // this guards a path that is not taken yet rather than one that is.
        builder.Property(x => x.ChannelId).ValueGeneratedNever();

        builder.Property(x => x.SpaceId).IsRequired();
        builder.Property(x => x.LastMessageId).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Every read of this table is "the marks for these spaces" — the badge aggregation across a
        // user's spaces, and the space snapshot for one. Both are an index seek and a narrow row
        // fetch. Not a covering index: the row is four columns wide, so storing LastMessageId in the
        // index would nearly duplicate the table to save a lookup that costs almost nothing, and
        // STORING/INCLUDE spelling differs between the two engines this has to run on.
        builder.HasIndex(x => x.SpaceId)
           .HasDatabaseName("ix_channel_last_messages_space");
    }
}
