namespace Argon;

[TsInterface, MessagePackObject(true)]
public record CreateServerRequest(
    string Name,
    string Description,
    string AvatarFileId);