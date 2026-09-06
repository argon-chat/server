namespace Argon.Services.Ion;

using Core.Grains.Interfaces;
using ion.runtime;
using NodaTime;
using System.Collections.Generic;

public class FriendsInteractionImpl : IFriendsInteraction
{
    public async Task<IonArray<UserBlock>> GetBlockList(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).GetBlockListAsync(limit, offset, ct);

    public async Task<IonArray<FriendRequest>> GetMyFriendPendingList(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).GetMyFriendPendingListAsync(limit, offset, ct);

    public async Task<IonArray<FriendRequest>> GetMyFriendOutgoingList(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).GetMyFriendOutgoingListAsync(limit, offset, ct);

    public async Task<IonArray<Friendship>> GetMyFriendships(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).GetMyFriendshipsAsync(limit, offset, ct);

    public async Task<SendFriendStatus> SendFriendRequest(string username, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).SendFriendRequestAsync(username, ct);

    public async Task RemoveFriend(Guid userId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).RemoveFriendAsync(userId, ct);

    public async Task AcceptFriendRequest(Guid fromUserId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).AcceptFriendRequestAsync(fromUserId, ct);

    public async Task DeclineFriendRequest(Guid fromUserId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).DeclineFriendRequestAsync(fromUserId, ct);

    public async Task CancelFriendRequest(Guid toUserId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).CancelFriendRequestAsync(toUserId, ct);

    public async Task BlockUser(Guid userId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).BlockUserAsync(userId, ct);

    public async Task UnblockUser(Guid userId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).UnblockUserAsync(userId, ct);

    public async Task<IonArray<UserIgnore>> GetIgnoreList(int limit, int offset, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).GetIgnoreListAsync(limit, offset, ct);

    public async Task IgnoreUser(Guid userId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).IgnoreUserAsync(userId, ct);

    public async Task UnignoreUser(Guid userId, CancellationToken ct = default)
        => await this.GetGrain<IFriendsGrain>(Guid.CreateVersion7()).UnignoreUserAsync(userId, ct);
}