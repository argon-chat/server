namespace Argon.Core.Features.Logic;

using Core.Entities.Data;

public record ReadStateEntry(Guid ChannelId, Guid? SpaceId, long LastReadMessageId, int MentionCount);

public interface IReadStateService
{
    Task AckAsync(Guid userId, Guid channelId, Guid? spaceId, long messageId, CancellationToken ct = default);
    Task IncrementMentionsAsync(Guid userId, Guid channelId, Guid? spaceId, int delta = 1, CancellationToken ct = default);
    Task BatchIncrementMentionsAsync(Guid spaceId, Guid channelId, IReadOnlyList<Guid> userIds, CancellationToken ct = default);
    Task<List<ReadStateEntry>> GetReadStatesForSpaceAsync(Guid userId, Guid spaceId, CancellationToken ct = default);
    Task<List<ReadStateEntry>> GetAllReadStatesAsync(Guid userId, CancellationToken ct = default);
}
