namespace Argon.Streaming.Events;

using Users;

[TsInterface, MessagePackObject(true)]
public record UserChangedStatus(Guid userId, UserStatus status, PropertyBag bag)
    : ArgonEvent<UserChangedStatus>;

[TsInterface, MessagePackObject(true)]
public record WelcomCommander(string happyball)
    : ArgonEvent<WelcomCommander>;