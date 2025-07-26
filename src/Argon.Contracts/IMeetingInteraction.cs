namespace Argon;

using Services.Transport;

public interface IMeetingInteraction : IArgonService
{
    [Alias("JoinAsync")]
    Task<Either<JoinMeetResponse, MeetJoinError>>
        JoinAsync([AsGrainId] string inviteCode, string username);

    [Alias("CreateMeetingLinkAsync"), UseRandomGrainId]
    Task<string> CreateMeetingLinkAsync();

    [Alias("SetDefaultPermissionsAsync")]
    Task SetDefaultPermissionsAsync([AsGrainId] string roomId, long permissions);

    [Alias("BeginRecordAsync")]
    Task BeginRecordAsync([AsGrainId] string roomId);

    [Alias("EndRecordAsync")]
    Task EndRecordAsync([AsGrainId] string roomId, string egressId);

    [Alias("MuteParticipantAsync")]
    Task MuteParticipantAsync([AsGrainId] string roomId, Guid participantId, bool isMuted);

    [Alias("DisableVideoAsync")]
    Task DisableVideoAsync([AsGrainId] string roomId, Guid participantId, bool isDisabled);
}

public enum MeetJoinError
{
    OK,
    NO_LINK_EXIST,
    YOU_ARE_BANNED
}

[MessagePackObject(true)]
public record JoinMeetResponse(string voiceToken, RtcEndpoint endpoint, MeetInfo meetInfo);

[MessagePackObject(true)]
public record MeetInfo(string title, int startTime, string roomId);

[MessagePackObject(true)]
public record RtcEndpoint(string endpoint, List<IceEndpoint> ices);

[MessagePackObject(true)]
public record IceEndpoint(string endpoint, string username, string password);