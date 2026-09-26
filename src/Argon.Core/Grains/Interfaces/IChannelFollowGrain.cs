namespace Argon.Grains.Interfaces;

/// <summary>
/// The follow links of one channel, keyed by its id. As a target it accepts new sources one at a
/// time, which is what keeps the per-target limit exact.
/// </summary>
[Alias("Argon.Grains.Interfaces.IChannelFollowGrain")]
public interface IChannelFollowGrain : IGrainWithGuidKey
{
    /// <summary>Keyed by the TARGET: makes it follow the source announcement channel.</summary>
    [Alias(nameof(FollowAsync))]
    Task<IFollowChannelResult> FollowAsync(Guid callerId, Guid sourceSpaceId, Guid sourceChannelId, Guid targetSpaceId);

    /// <summary>Keyed by the SOURCE: the channels following it. Empty without ManageChannels.</summary>
    [Alias(nameof(GetFollowersAsync))]
    Task<List<ChannelFollowLink>> GetFollowersAsync(Guid callerId, Guid spaceId);

    /// <summary>Keyed by the TARGET: the channels it follows. Empty without ViewChannel.</summary>
    [Alias(nameof(GetFollowedSourcesAsync))]
    Task<List<ChannelFollowLink>> GetFollowedSourcesAsync(Guid callerId, Guid spaceId);

    /// <summary>Keyed by either side: removes a link that involves this channel, with ManageChannels here.</summary>
    [Alias(nameof(RemoveAsync))]
    Task<IRemoveFollowResult> RemoveAsync(Guid callerId, Guid spaceId, Guid followId);
}

/// <summary>What a publish hands the fan-out: the copy and the channels it goes to.</summary>
[GenerateSerializer, Immutable]
public sealed record CrosspostBatch(
    [property: Id(0)] CrosspostDraft Draft,
    [property: Id(1)] List<Guid> Targets,
    [property: Id(2)] DateTimeOffset PublishedAt);

/// <summary>A crosspost copy before a target inserts it. Entities are already stripped of mentions.</summary>
[GenerateSerializer, Immutable]
public sealed record CrosspostDraft(
    [property: Id(0)] Guid AuthorId,
    [property: Id(1)] string Text,
    [property: Id(2)] List<IMessageEntity> Entities,
    [property: Id(3)] MessageCrosspost Source);
