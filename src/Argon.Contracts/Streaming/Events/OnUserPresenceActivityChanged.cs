namespace Argon.Streaming.Events;

using Users;

[TsInterface, MessagePackObject(true)]
public record OnUserPresenceActivityChanged(Guid userId, UserActivityPresence presence) : ArgonEvent<OnUserPresenceActivityChanged>;
[TsInterface, MessagePackObject(true)]
public record OnUserPresenceActivityRemoved(Guid userId) : ArgonEvent<OnUserPresenceActivityRemoved>;