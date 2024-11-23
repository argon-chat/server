namespace Argon.Contracts;

using ActualLab.Collections;
using ActualLab.Rpc;
using MessagePack;
using Orleans;
using Reinforced.Typings.Attributes;
using System.Reactive;
using Models;

[TsInterface]
public interface IUserInteraction : IRpcService
{
    Task<User>           GetMe();
    Task<Server>       CreateServer(CreateServerRequest request);
    Task<List<Server>> GetServers();
}

[TsInterface]
public interface IServerInteraction : IRpcService
{
    Task<CreateChannelResponse> CreateChannel(CreateChannelRequest request);
    Task                        DeleteChannel(DeleteChannelRequest request);
    Task<ChannelJoinResponse>   JoinToVoiceChannel(JoinToVoiceChannelRequest request);
}

[TsInterface, MessagePackObject(true)]
public record CreateChannelRequest(Guid serverId, string name, ChannelType kind, string desc);

[TsInterface, MessagePackObject(true)]
public record CreateChannelResponse(Guid serverId, Guid channelId);

[TsInterface, MessagePackObject(true)]
public record DeleteChannelRequest(Guid serverId, Guid channelId);

[TsInterface, MessagePackObject(true)]
public record JoinToVoiceChannelRequest(Guid serverId, Guid channelId);

[TsInterface]
public interface IEventBus : IRpcService
{
    ValueTask<RpcStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId);
}

[TsEnum]
public enum ChannelType : ushort
{
    Text,
    Voice,
    Announcement
}

[TsEnum]
public enum ServerEventKind
{
}

[TsInterface]
public interface IArgonEvent
{
    public static string ProviderId => "argon.cluster.events";
    public static string Namespace  => $"@";
}

[TsInterface, MessagePackObject(true)]
public record ArgonEvent<T> : IArgonEvent where T : ArgonEvent<T>, IArgonEvent;

[TsInterface, MessagePackObject(true)]
public record JoinToServerUser(Guid userId) : ArgonEvent<JoinToServerUser>;

[TsInterface, MessagePackObject(true)]
public record LeaveFromServerUser(Guid userId) : ArgonEvent<LeaveFromServerUser>;

[TsInterface, MessagePackObject(true)]
public record JoinedToChannelUser(Guid userId, Guid channelId) : ArgonEvent<JoinedToChannelUser>;

[TsInterface, MessagePackObject(true)]
public record LeavedFromChannelUser(Guid userId, Guid channelId) : ArgonEvent<LeavedFromChannelUser>;

[TsInterface, MessagePackObject(true)]
public record ChannelCreated(Channel channel) : ArgonEvent<ChannelCreated>;

[TsInterface, MessagePackObject(true)]
public record ChannelModified(Guid channelId, PropertyBag bag) : ArgonEvent<ChannelModified>;

[TsInterface, MessagePackObject(true)]
public record ChannelRemoved(Guid channelId) : ArgonEvent<ChannelRemoved>;

[TsInterface, MessagePackObject(true)]
public record UserChangedStatus(Guid userId, UserStatus status, PropertyBag bag)
    : ArgonEvent<UserChangedStatus>;

[TsInterface, MessagePackObject(true)]
public record ServerModified(PropertyBag bag) : ArgonEvent<ServerModified>;

[TsEnum]
public enum UserStatus
{
    Offline,
    Online,
    Away,
    InGame,
    Listen,
    TouchGrass,
    DoNotDisturb
}

[TsInterface, MessagePackObject(true)]
public sealed record ServerDetailsRequest(Guid ServerId);

[TsInterface, MessagePackObject(true)]
public record CreateServerRequest(
    string Name,
    string Description,
    string AvatarFileId);

[TsInterface, MessagePackObject(true)]
public record ChannelRealtimeMember(Guid UserId);

[TsInterface, MessagePackObject(true)]
public record ChannelJoinRequest(
    Guid ServerId,
    Guid ChannelId);

[TsInterface, MessagePackObject(true)]
public sealed record ChannelJoinResponse(
    string Token);