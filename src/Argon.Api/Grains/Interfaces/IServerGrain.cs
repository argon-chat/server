namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.IServerGrain")]
public interface IServerGrain : IGrainWithGuidKey
{
    [Alias("CreateServer")]
    Task<Either<Server, ServerCreationError>> CreateServer(ServerInput input, Guid creatorId);

    [Alias("GetServer")]
    Task<Server> GetServer();

    [Alias("UpdateServer")]
    Task<Server> UpdateServer(ServerInput input);

    [Alias("DeleteServer")]
    Task DeleteServer();

    [Alias("CreateChannel")]
    Task<Channel> CreateChannel(ChannelInput input, Guid initiator);

    [Alias("DeleteChannel")]
    Task DeleteChannel(Guid channelId, Guid initiator);

    [Alias("SetUserStatus")]
    ValueTask SetUserStatus(Guid userId, UserStatus status);

    [Alias("GetMembers")]
    Task<List<RealtimeServerMember>> GetMembers();

    [Alias("GetChannels")]
    Task<List<RealtimeChannel>> GetChannels();

    [Alias("DoJoinUserAsync")]
    ValueTask DoJoinUserAsync(Guid userId);

    public const string ProviderId = "argon.server.grain.stream";
    public const string EventNamespace = "@";
}

public enum ServerCreationError
{
    BAD_MODEL
}

[MessagePackObject(true)]
public sealed partial record ServerInput(
    string? Name,
    string? Description,
    string? AvatarUrl);