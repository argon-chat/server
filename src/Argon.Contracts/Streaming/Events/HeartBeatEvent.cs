namespace Argon.Streaming.Events;

using Users;

public record HeartBeatEvent(UserStatus status) : ArgonEvent<HeartBeatEvent>;