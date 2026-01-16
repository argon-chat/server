namespace Argon.Services.Ion;

using Core.Grains.Interfaces;
using ion.runtime;

public class UserChatInteractionImpl : IUserChatInteractions
{
    private IUserChatGrain UserChatGrain => this.GetGrain<IUserChatGrain>(Guid.CreateVersion7());

    public async Task<IonArray<UserChat>> GetRecentChats(int limit, int offset, CancellationToken ct = default)
        => await UserChatGrain.GetRecentChatsAsync(limit, offset, ct);

    public async Task PinChat(Guid peerId, CancellationToken ct = default)
        => await UserChatGrain.PinChatAsync(peerId, ct);

    public async Task UnpinChat(Guid peerId, CancellationToken ct = default)
        => await UserChatGrain.UnpinChatAsync(peerId, ct);

    public async Task MarkChatRead(Guid peerId, CancellationToken ct = default)
        => await UserChatGrain.MarkChatReadAsync(peerId, ct);

    public async Task<long> SendDirectMessage(Guid receiverId, string text, IonArray<IMessageEntity> entities, long randomId, long? replyTo, CancellationToken ct = default)
        => await UserChatGrain.SendDirectMessageAsync(receiverId, text, entities.Values.ToList(), randomId, replyTo, ct);

    public async Task<IonArray<DirectMessage>> QueryDirectMessages(Guid peerId, long? from, int limit, CancellationToken ct = default)
        => (await UserChatGrain.QueryDirectMessagesAsync(peerId, from, limit, ct));
}