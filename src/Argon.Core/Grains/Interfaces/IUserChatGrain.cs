namespace Argon.Core.Grains.Interfaces;

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
}