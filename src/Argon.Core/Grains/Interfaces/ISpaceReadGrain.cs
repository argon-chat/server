namespace Argon.Grains.Interfaces;

using Users;

/// <summary>
/// The read side of a space: who is in it, what channels it has, how they are grouped.
/// </summary>
/// <remarks>
/// Separate from <see cref="ISpaceGrain"/> because these answers are queries — they touch none of
/// that grain's state — and because sharing its activation made them queue behind each other. A
/// space grain is turn-based for a good reason: it serialises the writes. Reads have no such need
/// and were paying for it, three round trips per arriving client, one after another.
/// <para>
/// <c>[StatelessWorker]</c> is what supplies the parallelism: Orleans keeps a pool of activations
/// per silo — <c>ProcessorCount</c> of them unless told otherwise — and picks a free one, so reads
/// run concurrently but never more concurrently than the pool allows.
/// </para>
/// <para>
/// <b>Leave the bound alone.</b> It is not what is slow; it is what keeps the thing underneath from
/// being overwhelmed, and both ways of removing it have been measured. <c>[AlwaysInterleave]</c>
/// took 50 arriving clients from 836 ms to 9658 ms and failed outright at 150.
/// <c>[StatelessWorker(256)]</c> failed every one of 150 calls, with Orleans reporting a single
/// activation holding one request for 26 seconds and 276 queued behind it while Npgsql had run out
/// of connections. In both cases the contention moved to the database pool, where a request times
/// out rather than waits.
/// </para>
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(ISpaceReadGrain)}")]
public interface ISpaceReadGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Everything the first screen needs, minus whatever the caller says it already holds.
    /// </summary>
    /// <remarks>
    /// The client persists all of this and asks again on every sign-in, so "nothing moved" is the
    /// common answer and the one worth making cheap. Each part carries a content token; a caller that
    /// hands back a matching one gets <c>null</c> for that part and the server does not serialise it.
    /// </remarks>
    [Alias(nameof(GetSnapshot))]
    Task<SpaceSnapshot> GetSnapshot(SpaceVersions? known);

    /// <summary>
    /// Who is online, separately, because it changes every few seconds and would otherwise keep the
    /// snapshot's token from ever matching.
    /// </summary>
    [Alias(nameof(GetPresence))]
    Task<List<MemberPresence>> GetPresence();

    /// <summary>Superseded by <see cref="GetSnapshot"/>; remove once no shipped client calls it.</summary>
    [Alias(nameof(GetMembers))]
    Task<List<RealtimeServerMember>> GetMembers();

    /// <summary>Superseded by <see cref="GetSnapshot"/>; remove once no shipped client calls it.</summary>
    [Alias(nameof(GetChannels))]
    Task<List<RealtimeChannel>> GetChannels();

    /// <summary>Superseded by <see cref="GetSnapshot"/>; remove once no shipped client calls it.</summary>
    [Alias(nameof(GetChannelGroups))]
    Task<List<ChannelGroup>> GetChannelGroups();
}
