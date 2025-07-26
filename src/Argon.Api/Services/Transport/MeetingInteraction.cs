namespace Argon.Services;

public class MeetingInteraction : IMeetingInteraction
{
    public Task<Either<JoinMeetResponse, MeetJoinError>> JoinAsync(string inviteCode, string username)
        => this.GetGrainFactory().GetGrain<IMeetGrain>(inviteCode).JoinAsync(username);

    public Task<string> CreateMeetingLinkAsync()
        => throw new NotImplementedException();

    public Task SetDefaultPermissionsAsync(string roomId, long permissions)
        => this.GetGrainFactory().GetGrain<IMeetGrain>(roomId).SetDefaultPermissionsAsync(permissions);

    public Task BeginRecordAsync(string roomId)
        => this.GetGrainFactory().GetGrain<IMeetGrain>(roomId).BeginRecordAsync();

    public Task EndRecordAsync(string roomId, string egressId)
        => this.GetGrainFactory().GetGrain<IMeetGrain>(roomId).EndRecordAsync(egressId);

    public Task MuteParticipantAsync(string roomId, Guid participantId, bool isMuted)
        => throw new NotImplementedException();

    public Task DisableVideoAsync(string roomId, Guid participantId, bool isDisabled)
        => throw new NotImplementedException();
}