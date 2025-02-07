namespace Argon.Grains.Interfaces;

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

    [Alias(nameof(GetMembers))]
    Task<List<RealtimeServerMember>> GetMembers();

    [Alias(nameof(GetChannels))]
    Task<List<RealtimeChannel>> GetChannels();

    [Alias(nameof(DoJoinUserAsync))]
    ValueTask DoJoinUserAsync(Guid userId);
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