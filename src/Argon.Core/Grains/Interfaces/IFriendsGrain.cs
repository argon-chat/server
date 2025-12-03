namespace Argon.Core.Grains.Interfaces;

public interface IFriendsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetBlockListAsync))]
    Task<List<UserBlock>> GetBlockListAsync(int limit, int offset, CancellationToken ct = default);

    [Alias(nameof(GetMyFriendPendingListAsync))]
    Task<List<FriendRequest>> GetMyFriendPendingListAsync(int limit, int offset, CancellationToken ct = default);

    [Alias(nameof(GetMyFriendOutgoingListAsync))]
    Task<List<FriendRequest>> GetMyFriendOutgoingListAsync(int limit, int offset, CancellationToken ct = default);

    [Alias(nameof(GetMyFriendshipsAsync))]
    Task<List<Friendship>> GetMyFriendshipsAsync(int limit, int offset, CancellationToken ct = default);

    [Alias(nameof(SendFriendRequestAsync))]
    Task<SendFriendStatus> SendFriendRequestAsync(string username, CancellationToken ct = default);

    [Alias(nameof(RemoveFriendAsync))]
    Task RemoveFriendAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(AcceptFriendRequestAsync))]
    Task AcceptFriendRequestAsync(Guid fromUserId, CancellationToken ct = default);

    [Alias(nameof(DeclineFriendRequestAsync))]
    Task DeclineFriendRequestAsync(Guid fromUserId, CancellationToken ct = default);

    [Alias(nameof(CancelFriendRequestAsync))]
    Task CancelFriendRequestAsync(Guid toUserId, CancellationToken ct = default);

    [Alias(nameof(BlockUserAsync))]
    Task BlockUserAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(UnblockUserAsync))]
    Task UnblockUserAsync(Guid userId, CancellationToken ct = default);
}