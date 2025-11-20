namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.IMeetGrain")]
public interface IMeetGrain : IGrainWithStringKey
{
    [Alias("CreateMeetingLinkAsync")]
    Task<string> CreateMeetingLinkAsync();

    [Alias("SetDefaultPermissionsAsync")]
    Task SetDefaultPermissionsAsync(long permissions);

    [Alias("BeginRecordAsync")]
    Task<string> BeginRecordAsync();

    [Alias("EndRecordAsync")]
    Task<string> EndRecordAsync(string egressId);

    [Alias("MuteParticipantAsync")]
    Task MuteParticipantAsync(Guid participantId, bool isMuted);

    [Alias("DisableVideoAsync")]
    Task DisableVideoAsync(Guid participantId, bool isDisabled);
}
