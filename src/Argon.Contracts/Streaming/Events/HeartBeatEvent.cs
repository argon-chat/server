namespace Argon.Streaming.Events;

using Users;

public record HeartBeatEvent(UserStatus status) : ArgonEvent<HeartBeatEvent>;

[MessagePackObject(true)]
public record IAmTypingEvent(Guid serverId, Guid channelId) : ArgonEvent<IAmTypingEvent>;
[MessagePackObject(true)]
public record IAmStopTypingEvent(Guid serverId, Guid channelId) : ArgonEvent<IAmStopTypingEvent>;


[MessagePackObject(true)]
public record UserTypingEvent(Guid userId, Guid serverId, Guid channelId) : ArgonEvent<UserTypingEvent>;
[MessagePackObject(true)]
public record UserStopTypingEvent(Guid userId, Guid serverId, Guid channelId) : ArgonEvent<UserStopTypingEvent>;