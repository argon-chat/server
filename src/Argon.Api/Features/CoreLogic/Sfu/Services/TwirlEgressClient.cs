namespace Argon.Sfu.Services;

using Grpc.Core;
using LiveKit.Proto;

public class TwirlEgressClient(TwirpClient client) : Egress.EgressClient
{
    public override AsyncUnaryCall<EgressInfo> StartRoomCompositeEgressAsync(RoomCompositeEgressRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<RoomCompositeEgressRequest, EgressInfo>("StartRoomCompositeEgress", request, headers, cancellationToken);

    public override AsyncUnaryCall<ListEgressResponse> ListEgressAsync(ListEgressRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<ListEgressRequest, ListEgressResponse>("ListEgress", request, headers, cancellationToken);

    public override AsyncUnaryCall<EgressInfo> StartParticipantEgressAsync(ParticipantEgressRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<ParticipantEgressRequest, EgressInfo>("StartParticipantEgress", request, headers, cancellationToken);

    public override AsyncUnaryCall<EgressInfo> StartWebEgressAsync(WebEgressRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<WebEgressRequest, EgressInfo>("StartWebEgress", request, headers, cancellationToken);

    public override AsyncUnaryCall<EgressInfo> StopEgressAsync(StopEgressRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<StopEgressRequest, EgressInfo>("StopEgress", request, headers, cancellationToken);

    public override AsyncUnaryCall<EgressInfo> UpdateLayoutAsync(UpdateLayoutRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<UpdateLayoutRequest, EgressInfo>("UpdateLayout", request, headers, cancellationToken);

    public override AsyncUnaryCall<EgressInfo> UpdateStreamAsync(UpdateStreamRequest request, Metadata? headers = null, DateTime? deadline = null,
        CancellationToken cancellationToken = default)
        => Wrap<UpdateStreamRequest, EgressInfo>("UpdateStream", request, headers, cancellationToken);

    private AsyncUnaryCall<TResponse> Wrap<TRequest, TResponse>(string methodName, TRequest request, Metadata? headers, CancellationToken ct)
    {
        var token = headers?.GetValue("authorization")?.Replace("Bearer ", "");
        var task = client.CallAsync<TRequest, TResponse>("livekit.EgressService", methodName, request!, token, ct);
        return new AsyncUnaryCall<TResponse>(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }
}
