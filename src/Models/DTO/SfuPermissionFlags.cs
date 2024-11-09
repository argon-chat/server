namespace Models.DTO;

[Flags]
public enum SfuPermissionFlags
{
    NONE = 0,
    [FlagName("roomCreate")]
    ROOM_CREATE = 1 << 1,
    [FlagName("roomJoin")]
    ROOM_JOIN = 1 << 2,
    [FlagName("canUpdateOwnMetadata")]
    UPDATE_METADATA = 1 << 3,
    [FlagName("roomList")]
    ROOM_LIST = 1 << 4,
    [FlagName("roomRecord")]
    ROOM_RECORD = 1 << 5,
    [FlagName("roomAdmin")]
    ROOM_ADMIN = 1 << 6,
    [FlagName("canPublish")]
    CAN_PUBLISH = 1 << 7,
    [FlagName("canSubscribe")]
    CAN_LISTEN = 1 << 8,
    [FlagName("hidden")]
    HIDDEN = 1 << 9,
    ALL = ROOM_CREATE | ROOM_JOIN | UPDATE_METADATA | ROOM_LIST | CAN_PUBLISH | ROOM_RECORD | ROOM_ADMIN | CAN_LISTEN
}