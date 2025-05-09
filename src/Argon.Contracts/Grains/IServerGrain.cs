namespace Argon.Grains.Interfaces;

using Users;

[Alias($"Argon.Grains.Interfaces.{nameof(IServerGrain)}")]
public interface IServerGrain : IGrainWithGuidKey
{
    [Alias(nameof(CreateServer))]
    Task<Either<Server, ServerCreationError>> CreateServer(ServerInput input, Guid creatorId);

    [Alias(nameof(GetServer))]
    Task<Server> GetServer();

    [Alias(nameof(UpdateServer))]
    Task<Server> UpdateServer(ServerInput input);

    [Alias(nameof(DeleteServer))]
    Task DeleteServer();

    [Alias(nameof(CreateChannel))]
    Task<Channel> CreateChannel(ChannelInput input, Guid initiator);

    [Alias(nameof(DeleteChannel))]
    Task DeleteChannel(Guid channelId, Guid initiator);

    [Alias(nameof(SetUserStatus))]
    ValueTask SetUserStatus(Guid userId, UserStatus status);

    [Alias(nameof(SetUserPresence))]
    ValueTask SetUserPresence(Guid userId, UserActivityPresence presence);

    [Alias(nameof(RemoveUserPresence))]
    ValueTask RemoveUserPresence(Guid userId);

    [Alias(nameof(GetMembers))]
    Task<List<RealtimeServerMember>> GetMembers();

    [Alias(nameof(GetMember))]
    Task<RealtimeServerMember> GetMember(Guid userId);

    [Alias(nameof(GetChannels))]
    Task<List<RealtimeChannel>> GetChannels();

    [Alias(nameof(DoJoinUserAsync))]
    ValueTask DoJoinUserAsync(Guid userId);

    [Alias(nameof(DoUserUpdatedAsync))]
    ValueTask DoUserUpdatedAsync(Guid userId);

    [Alias(nameof(PrefetchProfile))]
    ValueTask<UserProfileDto> PrefetchProfile(Guid userId, Guid caller);
}

public enum ServerCreationError
{
    BAD_MODEL
}

[MessagePackObject(true)]
public sealed record ServerInput(
    string? Name,
    string? Description,
    string? AvatarUrl);