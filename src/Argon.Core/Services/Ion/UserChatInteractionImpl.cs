namespace Argon.Services.Ion;

using Core.Grains.Interfaces;
using ion.runtime;

public class UserChatInteractionImpl : IUserChatInteractions
{
    public async Task<IonArray<UserChat>> GetRecentChats(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).GetRecentChatsAsync(limit, offset, ct);

    public async Task PinChat(Guid peerId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).PinChatAsync(peerId, ct);

    public async Task UnpinChat(Guid peerId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).UnpinChatAsync(peerId, ct);

    public async Task MarkChatRead(Guid peerId, CancellationToken ct = default)
        => await this.GetGrain<IUserChatGrain>(Guid.CreateVersion7()).MarkChatReadAsync(peerId, ct);
}