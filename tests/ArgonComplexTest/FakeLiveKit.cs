namespace ArgonComplexTest;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Livekit.Server.Sdk.Dotnet;

/// <summary>
/// LiveKit's Twirp API answered in-process: every RoomService and Egress call is recorded and
/// succeeds, unless <see cref="FailingMethods"/> names it.
/// </summary>
/// <remarks>
/// The SDK clients are sealed over an <see cref="HttpClient"/>, so this is the seam: the test host
/// builds them with a client over this handler. The SDK speaks protobuf, and an empty body is a
/// valid encoding of every response message, so only calls whose answer is read need a real one.
/// </remarks>
public sealed class FakeLiveKit : HttpMessageHandler
{
    private readonly ConcurrentQueue<(string Method, byte[] Body)> calls = new();

    /// <summary>Twirp method names (e.g. <c>UpdateParticipant</c>) answered with a <c>not_found</c> error.</summary>
    public ConcurrentDictionary<string, bool> FailingMethods { get; } = new();

    public IReadOnlyList<UpdateParticipantRequest> UpdateParticipantCalls
        => Calls("UpdateParticipant", UpdateParticipantRequest.Parser);

    public IReadOnlyList<RoomParticipantIdentity> RemoveParticipantCalls
        => Calls("RemoveParticipant", RoomParticipantIdentity.Parser);

    public IReadOnlyList<RoomCompositeEgressRequest> StartEgressCalls
        => Calls("StartRoomCompositeEgress", RoomCompositeEgressRequest.Parser);

    public IReadOnlyList<StopEgressRequest> StopEgressCalls
        => Calls("StopEgress", StopEgressRequest.Parser);

    private IReadOnlyList<T> Calls<T>(string method, MessageParser<T> parser) where T : IMessage<T>
        => calls.Where(c => c.Method == method).Select(c => parser.ParseFrom(c.Body)).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.Segments[^1];
        var body   = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        calls.Enqueue((method, body));

        if (FailingMethods.ContainsKey(method))
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"code":"not_found","msg":"participant not found"}""",
                    new MediaTypeHeaderValue("application/json"))
            };

        var answer = method switch
        {
            "StartRoomCompositeEgress" => new EgressInfo { EgressId = $"EG_{Guid.NewGuid():N}" }.ToByteArray(),
            "StopEgress"               => new EgressInfo { EgressId = StopEgressRequest.Parser.ParseFrom(body).EgressId }.ToByteArray(),
            _                          => []
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(answer) { Headers = { ContentType = new MediaTypeHeaderValue("application/protobuf") } }
        };
    }
}
