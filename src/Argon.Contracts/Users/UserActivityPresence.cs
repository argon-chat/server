namespace Argon.Users;

[TsInterface, MessagePackObject(true)]
public record UserActivityPresence(
    ActivityPresenceKind Kind,
    long StartTimestampSeconds,
    string TitleName
);

public enum ActivityPresenceKind
{
    GAME,
    SOFTWARE,
    STREAMING
}