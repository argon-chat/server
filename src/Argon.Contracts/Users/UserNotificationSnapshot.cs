namespace Argon.Users;

[MessagePackObject(true), TsInterface]
public record UserNotificationSnapshot(List<UserNotificationItem> mentions);

[MessagePackObject(true), TsInterface]
public record UserNotificationItem(Guid serverId, int mentionCount);