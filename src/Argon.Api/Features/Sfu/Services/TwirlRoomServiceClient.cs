namespace Argon.Sfu.Services;

using Grpc.Core;
using LiveKit.Proto;

public class TwirlRoomServiceClient(TwirpClient client) : RoomService.RoomServiceClient
{
    public override AsyncUnaryCall<RemoveParticipantResponse> RemoveParticipantAsync(RoomParticipantIdentity request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<RoomParticipantIdentity, RemoveParticipantResponse>("RemoveParticipant", request, headers, cancellationToken);

    public override AsyncUnaryCall<Room> CreateRoomAsync(CreateRoomRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<CreateRoomRequest, Room>("CreateRoom", request, headers, cancellationToken);

    public override AsyncUnaryCall<DeleteRoomResponse> DeleteRoomAsync(DeleteRoomRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<DeleteRoomRequest, DeleteRoomResponse>("DeleteRoom", request, headers, cancellationToken);

    public override AsyncUnaryCall<ForwardParticipantResponse> ForwardParticipantAsync(ForwardParticipantRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<ForwardParticipantRequest, ForwardParticipantResponse>("ForwardParticipant", request, headers, cancellationToken);

    public override AsyncUnaryCall<ParticipantInfo> GetParticipantAsync(RoomParticipantIdentity request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<RoomParticipantIdentity, ParticipantInfo>("GetParticipant", request, headers, cancellationToken);

    public override AsyncUnaryCall<ListParticipantsResponse> ListParticipantsAsync(ListParticipantsRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<ListParticipantsRequest, ListParticipantsResponse>("ListParticipants", request, headers, cancellationToken);

    public override AsyncUnaryCall<ListRoomsResponse> ListRoomsAsync(ListRoomsRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<ListRoomsRequest, ListRoomsResponse>("ListRooms", request, headers, cancellationToken);

    public override AsyncUnaryCall<MoveParticipantResponse> MoveParticipantAsync(MoveParticipantRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<MoveParticipantRequest, MoveParticipantResponse>("MoveParticipant", request, headers, cancellationToken);

    public override AsyncUnaryCall<MuteRoomTrackResponse> MutePublishedTrackAsync(MuteRoomTrackRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<MuteRoomTrackRequest, MuteRoomTrackResponse>("MutePublishedTrack", request, headers, cancellationToken);

    private AsyncUnaryCall<TResponse> Wrap<TRequest, TResponse>(string methodName, TRequest request, Metadata? headers, CancellationToken ct)
    {
        var token = headers?.GetValue("authorization")?.Replace("Bearer ", "");
        var task = client.CallAsync<TRequest, TResponse>("livekit.RoomService", methodName, request!, token, ct);
        return new AsyncUnaryCall<TResponse>(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }
}
