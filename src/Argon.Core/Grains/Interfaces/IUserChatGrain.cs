namespace Argon.Core.Grains.Interfaces;

using Entities.Data;

[Alias(nameof(IUserChatGrain))]
public interface IUserChatGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetRecentChatsAsync))]
    Task<List<UserChat>> GetRecentChatsAsync(int limit, int offset, CancellationToken ct = default);

    [Alias(nameof(PinChatAsync))]
    Task PinChatAsync(Guid peerId, CancellationToken ct = default);

    [Alias(nameof(UnpinChatAsync))]
    Task UnpinChatAsync(Guid peerId, CancellationToken ct = default);

    [Alias(nameof(MarkChatReadAsync))]
    Task MarkChatReadAsync(Guid peerId, CancellationToken ct = default);

    [Alias(nameof(UpdateChatAsync))]
    Task UpdateChatAsync(Guid peerId, string? previewText, DateTimeOffset timestamp, CancellationToken ct = default);

    [Alias(nameof(UpdateChatForAsync))]
    Task UpdateChatForAsync(Guid userId, Guid peerId, string? previewText, DateTimeOffset timestamp, CancellationToken ct = default);

    [Alias(nameof(SendDirectMessageAsync))]
    Task<long> SendDirectMessageAsync(Guid receiverId, string text, List<IMessageEntity> entities, long randomId, long? replyTo, CancellationToken ct = default);

    [Alias(nameof(QueryDirectMessagesAsync))]
    Task<List<DirectMessage>> QueryDirectMessagesAsync(Guid peerId, long? from, int limit, CancellationToken ct = default);
}