namespace Argon.Sfu;

using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Flurl.Http;
using LiveKit.Proto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

#if DEBUG
public class ArgonSfuTestController : ControllerBase
{
    [HttpGet("/sfu/create_channel")]
    public async ValueTask<IActionResult> GetData([FromServices] IArgonSelectiveForwardingUnit sfu,
        [FromQuery] Guid serverId, [FromQuery] Guid channelId)
    {
        return Ok(await
            sfu.EnsureEphemeralChannelAsync(new ArgonChannelId(new ArgonServerId(serverId), channelId), 15));
    }

    [HttpPost("/sfu/token")]
    public async ValueTask<IActionResult> GetToken([FromServices] IArgonSelectiveForwardingUnit sfu,
        [FromBody] ArgonChannelId roomId)
    {
        return Ok(await
            sfu.IssueAuthorizationTokenAsync(new ArgonUserId(Guid.NewGuid()), roomId, SfuPermission.DefaultUser));
    }
}
#endif

public class ArgonSelectiveForwardingUnit(
    IOptions<SfuFeatureSettings> settings,
    [FromKeyedServices(SfuFeature.HttpClientKey)]
    IFlurlClient httpClient) : IArgonSelectiveForwardingUnit
{
    private static readonly Guid
        SystemUser = new([2, 26, 77, 5, 231, 16, 198, 72, 164, 29, 136, 207, 134, 192, 33, 33]);

    public ValueTask<RealtimeToken> IssueAuthorizationTokenAsync(ArgonUserId userId, ArgonChannelId channelId,
        SfuPermission permission) =>
        new(CreateJwt(channelId, userId, permission, settings)); // TODO check validity

    public ValueTask<bool> SetMuteParticipantAsync(bool isMuted, ArgonUserId userId, ArgonChannelId channelId)
        => throw new NotImplementedException(); // TODO

    public async ValueTask<bool> KickParticipantAsync(ArgonUserId userId, ArgonChannelId channelId)
    {
        await RequestAsync("RoomService", "RemoveParticipant", new RoomParticipantIdentity
        {
            Identity = userId.ToRawIdentity(),
            Room = channelId.ToRawRoomId()
        }, new Dictionary<string, string>
        {
            { "Authorization", $"Bearer {CreateSystemToken(channelId).value}" }
        });
        return true;
    }

    public async ValueTask<EphemeralChannelInfo> EnsureEphemeralChannelAsync(ArgonChannelId channelId,
        uint maxParticipants)
    {
        var result = await RequestAsync<CreateRoomRequest, Room>("RoomService", "CreateRoom", new CreateRoomRequest
        {
            Name = channelId.ToRawRoomId(),
            Metadata = channelId.ToRawRoomId(),
            MaxParticipants = maxParticipants,
            DepartureTimeout = 10,
            EmptyTimeout = 2,
            SyncStreams = true
        }, new Dictionary<string, string>
        {
            { "Authorization", $"Bearer {CreateSystemToken(channelId).value}" }
        });

        return new EphemeralChannelInfo(channelId, result.Sid, result);
    }

    public async ValueTask<bool> PruneEphemeralChannelAsync(ArgonChannelId channelId)
    {
        await RequestAsync("RoomService", "DeleteRoom", new DeleteRoomRequest
        {
            Room = channelId.ToRawRoomId()
        }, new Dictionary<string, string>
        {
            { "Authorization", $"Bearer {CreateSystemToken(channelId).value}" }
        });
        return true;
    }

    private RealtimeToken CreateSystemToken(ArgonChannelId channelId)
        => CreateJwt(channelId, new ArgonUserId(SystemUser), SfuPermission.DefaultSystem, settings);

    private static RealtimeToken CreateJwt(ArgonChannelId roomName, ArgonUserId identity, SfuPermission permissions,
        IOptions<SfuFeatureSettings> settings)
    {
        var now = DateTime.UtcNow;
        JwtHeader headers =
            new(new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Value.ClientSecret)),
                "HS256"));

        JwtPayload payload = new()
        {
            { "exp", new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds() },
            { "iss", settings.Value.ClientId },
            { "nbf", 0 },
            { "sub", identity.ToRawIdentity() },
            { "name", identity.ToRawIdentity() },
            { "video", permissions.ToDictionary(roomName) }
        };

        JwtSecurityToken token = new(headers, payload);
        return new RealtimeToken(new JwtSecurityTokenHandler().WriteToken(token));
    }

    private const string pkg = "livekit";
    private const string prefix = "/twirp";

    public async ValueTask<TResp> RequestAsync<TReq, TResp>(string service, string method, TReq data,
        Dictionary<string, string> headers, CancellationToken ct = default)
    {
        var response = await httpClient
            .Request($"{prefix}/{pkg}.{service}/{method}")
            .WithHeaders(headers)
            .AllowAnyHttpStatus()
            .PostJsonAsync(data, cancellationToken: ct);

        if (response.StatusCode != 200)
            throw new SfuRPCExceptions(response.StatusCode, await response.GetStringAsync());
        return await response.GetJsonAsync<TResp>();
    }

    public async ValueTask RequestAsync<TReq>(string service, string method, TReq data,
        Dictionary<string, string> headers, CancellationToken ct = default)
    {
        var response = await httpClient
            .Request($"{prefix}/{pkg}.{service}/{method}")
            .WithHeaders(headers)
            .AllowAnyHttpStatus()
            .PostJsonAsync(data, cancellationToken: ct);

        if (response.StatusCode != 200)
            throw new SfuRPCExceptions(response.StatusCode, await response.GetStringAsync());
    }

    private class SfuRPCExceptions(int statusCode, string message) : Exception;
}