namespace Argon.Entities;

using ion.runtime;

/// <summary>Broadcast ("radio") settings of a voice channel; null on the entity means an ordinary voice channel.</summary>
public sealed record ChannelBroadcast
{
    public const int MinDuckingDb          = -40;
    public const int MaxDuckingDb          = 0;
    public const int MinTransmitSeconds    = 10;
    public const int MaxTransmitSecondsCap = 3600;

    public List<Guid>       Targets            { get; init; } = [];
    public BroadcastOverlap Overlap            { get; init; } = BroadcastOverlap.MIX;
    public int              DuckingDb          { get; init; } = -8;
    public int?             MaxTransmitSeconds { get; init; } = 120;
    public bool             Chirp              { get; init; }

    /// <summary>What switching the mode on starts with.</summary>
    public static ChannelBroadcast Default() => new();

    /// <summary>
    /// Merges a sparse patch: a field the patch leaves out stays, a cleared one goes back to its
    /// default (no targets, MIX, -8 dB, no transmit limit, no chirp), numbers are clamped and targets
    /// de-duplicated. <c>InvalidTarget</c> is the channel targeting itself; whether the other targets
    /// exist is the grain's check.
    /// </summary>
    public (ChannelBroadcast Next, bool InvalidTarget) Apply(IonPartial<BroadcastSettings> patch, Guid channelId)
    {
        var next    = this;
        var invalid = false;

        patch.On(x => x.targets,
            onModified: v =>
            {
                var targets = (v.Values ?? []).Distinct().ToList();
                invalid = targets.Contains(channelId);
                next    = next with { Targets = targets };
            },
            onRemoved: () => next = next with { Targets = [] });
        patch.On(x => x.overlap,
            onModified: v => next = next with { Overlap = v },
            onRemoved: () => next = next with { Overlap = BroadcastOverlap.MIX });
        patch.On(x => x.duckingDb,
            onModified: v => next = next with { DuckingDb = Math.Clamp(v, MinDuckingDb, MaxDuckingDb) },
            onRemoved: () => next = next with { DuckingDb = -8 });
        patch.On(x => x.maxTransmitSeconds,
            onModified: v => next = next with { MaxTransmitSeconds = v is { } s ? Math.Clamp(s, MinTransmitSeconds, MaxTransmitSecondsCap) : null },
            onRemoved: () => next = next with { MaxTransmitSeconds = null });
        patch.On(x => x.chirp,
            onModified: v => next = next with { Chirp = v },
            onRemoved: () => next = next with { Chirp = false });

        return (next, invalid);
    }

    /// <summary>Field-wise equality; the record's own compares the target list by reference.</summary>
    public bool SameAs(ChannelBroadcast other)
        => Targets.SequenceEqual(other.Targets)
        && Overlap == other.Overlap
        && DuckingDb == other.DuckingDb
        && MaxTransmitSeconds == other.MaxTransmitSeconds
        && Chirp == other.Chirp;

    public static BroadcastSettings? ToDto(ChannelBroadcast? self)
        => self is null
            ? null
            : new BroadcastSettings(new IonArray<Guid>(self.Targets), self.Overlap, self.DuckingDb, self.MaxTransmitSeconds, self.Chirp);
}
