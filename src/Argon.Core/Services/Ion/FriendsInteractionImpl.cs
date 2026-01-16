namespace Argon.Services.Ion;

using Core.Grains.Interfaces;
using ion.runtime;
using NodaTime;
using System.Collections.Generic;

public class FriendsInteractionImpl : IFriendsInteraction
{
    private IFriendsGrain FriendsGrain => this.GetGrain<IFriendsGrain>(Guid.CreateVersion7());

    public async Task<IonArray<UserBlock>> GetBlockList(int limit, int offset, CancellationToken ct = default)
        => await FriendsGrain.GetBlockListAsync(limit, offset, ct);

    public async Task<IonArray<FriendRequest>> GetMyFriendPendingList(int limit, int offset, CancellationToken ct = default)
        => await FriendsGrain.GetMyFriendPendingListAsync(limit, offset, ct);

    public async Task<IonArray<FriendRequest>> GetMyFriendOutgoingList(int limit, int offset, CancellationToken ct = default)
        => await FriendsGrain.GetMyFriendOutgoingListAsync(limit, offset, ct);

    public async Task<IonArray<Friendship>> GetMyFriendships(int limit, int offset, CancellationToken ct = default)
        => await FriendsGrain.GetMyFriendshipsAsync(limit, offset, ct);

    public async Task<SendFriendStatus> SendFriendRequest(string username, CancellationToken ct = default)
        => await FriendsGrain.SendFriendRequestAsync(username, ct);

    public async Task RemoveFriend(Guid userId, CancellationToken ct = default)
        => await FriendsGrain.RemoveFriendAsync(userId, ct);

    public async Task AcceptFriendRequest(Guid fromUserId, CancellationToken ct = default)
        => await FriendsGrain.AcceptFriendRequestAsync(fromUserId, ct);

    public async Task DeclineFriendRequest(Guid fromUserId, CancellationToken ct = default)
        => await FriendsGrain.DeclineFriendRequestAsync(fromUserId, ct);

    public async Task CancelFriendRequest(Guid toUserId, CancellationToken ct = default)
        => await FriendsGrain.CancelFriendRequestAsync(toUserId, ct);

    public async Task BlockUser(Guid userId, CancellationToken ct = default)
        => await FriendsGrain.BlockUserAsync(userId, ct);

    public async Task UnblockUser(Guid userId, CancellationToken ct = default)
        => await FriendsGrain.UnblockUserAsync(userId, ct);
}