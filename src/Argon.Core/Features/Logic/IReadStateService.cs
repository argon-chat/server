namespace Argon.Core.Features.Logic;

using Core.Entities.Data;

public record ReadStateEntry(Guid ChannelId, Guid? SpaceId, long LastReadMessageId, int MentionCount);

public interface IReadStateService
{
    // The new state when the mark moved; null for a stale ack.
    Task<ReadStateEntry?> AckAsync(Guid userId, Guid channelId, Guid? spaceId, long messageId, CancellationToken ct = default);

    // Mentions count only for readers who have not read messageId yet.
    Task IncrementMentionsAsync(Guid userId, Guid channelId, Guid? spaceId, long messageId, int delta = 1, CancellationToken ct = default);
    Task BatchIncrementMentionsAsync(Guid spaceId, Guid channelId, long messageId, IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    // Set-based @everyone bump: enumerates members + applies mute/suppress exclusion entirely in
    // SQL. Never loads the member id list into the silo heap and issues no per-member writes.
    // Intended for very large spaces; smaller spaces keep the precise BatchIncrementMentionsAsync path.
    Task BumpEveryoneMentionsAsync(Guid spaceId, Guid channelId, Guid senderId, long messageId, CancellationToken ct = default);

    // After the channel's newest messages were deleted: whoever has read up to lastMessageId has
    // nothing unread left, so any mentions they still hold came from the deleted ones.
    Task ClearCaughtUpMentionsAsync(Guid channelId, long lastMessageId, CancellationToken ct = default);
    Task<List<ReadStateEntry>> GetReadStatesForSpaceAsync(Guid userId, Guid spaceId, CancellationToken ct = default);
    Task<List<ReadStateEntry>> GetAllReadStatesAsync(Guid userId, CancellationToken ct = default);
}
