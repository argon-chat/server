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
    [HttpGet(template: "/sfu/create_channel")]
    public async ValueTask<IActionResult> GetData([FromServices] IArgonSelectiveForwardingUnit sfu,
                                                  [FromQuery]    Guid                          serverId, [FromQuery] Guid channelId)
        => Ok(value: await
                         sfu.EnsureEphemeralChannelAsync(
                             channelId: new ArgonChannelId(serverId: new ArgonServerId(id: serverId), channelId: channelId),
                             maxParticipants: 15));

    [HttpPost(template: "/sfu/token")]
    public async ValueTask<IActionResult> GetToken([FromServices] IArgonSelectiveForwardingUnit sfu,
                                                   [FromBody]     ArgonChannelId                roomId)
        => Ok(value: await
                         sfu.IssueAuthorizationTokenAsync(userId: new ArgonUserId(id: Guid.NewGuid()), channelId: roomId,
                             permission: SfuPermission.DefaultUser));
}
#endif

public class ArgonSelectiveForwardingUnit(
    IOptions<SfuFeatureSettings> settings,
    [FromKeyedServices(key: SfuFeature.HttpClientKey)]
    IFlurlClient httpClient) : IArgonSelectiveForwardingUnit
{
    private const string pkg    = "livekit";
    private const string prefix = "/twirp";

    private static readonly Guid
        SystemUser = new(b: [2, 26, 77, 5, 231, 16, 198, 72, 164, 29, 136, 207, 134, 192, 33, 33]);

    public ValueTask<RealtimeToken> IssueAuthorizationTokenAsync(ArgonUserId   userId, ArgonChannelId channelId,
                                                                 SfuPermission permission)
        => new(result: CreateJwt(roomName: channelId, identity: userId, permissions: permission, settings: settings));

    // TODO check validity
    public ValueTask<bool> SetMuteParticipantAsync(bool isMuted, ArgonUserId userId, ArgonChannelId channelId)
        => throw new NotImplementedException();

    // TODO
    public async ValueTask<bool> KickParticipantAsync(ArgonUserId userId, ArgonChannelId channelId)
    {
        await RequestAsync(service: "RoomService", method: "RemoveParticipant", data: new RoomParticipantIdentity
        {
            Identity = userId.ToRawIdentity(),
            Room     = channelId.ToRawRoomId()
        }, headers: new Dictionary<string, string>
        {
            {
                "Authorization", $"Bearer {CreateSystemToken(channelId: channelId).value}"
            }
        });
        return true;
    }

    public async ValueTask<EphemeralChannelInfo> EnsureEphemeralChannelAsync(ArgonChannelId channelId,
                                                                             uint           maxParticipants)
    {
        var result = await RequestAsync<CreateRoomRequest, Room>(service: "RoomService", method: "CreateRoom", data: new CreateRoomRequest
        {
            Name             = channelId.ToRawRoomId(),
            Metadata         = channelId.ToRawRoomId(),
            MaxParticipants  = maxParticipants,
            DepartureTimeout = 10,
            EmptyTimeout     = 2,
            SyncStreams      = true
        }, headers: new Dictionary<string, string>
        {
            {
                "Authorization", $"Bearer {CreateSystemToken(channelId: channelId).value}"
            }
        });

        return new EphemeralChannelInfo(channelId: channelId, sid: result.Sid, room: result);
    }

    public async ValueTask<bool> PruneEphemeralChannelAsync(ArgonChannelId channelId)
    {
        await RequestAsync(service: "RoomService", method: "DeleteRoom", data: new DeleteRoomRequest
        {
            Room = channelId.ToRawRoomId()
        }, headers: new Dictionary<string, string>
        {
            {
                "Authorization", $"Bearer {CreateSystemToken(channelId: channelId).value}"
            }
        });
        return true;
    }

    private RealtimeToken CreateSystemToken(ArgonChannelId channelId)
        => CreateJwt(roomName: channelId, identity: new ArgonUserId(id: SystemUser), permissions: SfuPermission.DefaultSystem, settings: settings);

    private static RealtimeToken CreateJwt(ArgonChannelId               roomName, ArgonUserId identity, SfuPermission permissions,
                                           IOptions<SfuFeatureSettings> settings)
    {
        var now = DateTime.UtcNow;
        JwtHeader headers =
            new(signingCredentials: new
                SigningCredentials(key: new SymmetricSecurityKey(key: Encoding.UTF8.GetBytes(s: settings.Value.ClientSecret)),
                    algorithm: "HS256"));

        JwtPayload payload = new()
        {
            {
                "exp", new DateTimeOffset(dateTime: now.AddHours(value: 1)).ToUnixTimeSeconds()
            },
            {
                "iss", settings.Value.ClientId
            },
            {
                "nbf", 0
            },
            {
                "sub", identity.ToRawIdentity()
            },
            {
                "name", identity.ToRawIdentity()
            },
            {
                "video", permissions.ToDictionary(channelId: roomName)
            }
        };

        JwtSecurityToken token = new(header: headers, payload: payload);
        return new RealtimeToken(value: new JwtSecurityTokenHandler().WriteToken(token: token));
    }

    public async ValueTask<TResp> RequestAsync<TReq, TResp>(string                     service, string            method, TReq data,
                                                            Dictionary<string, string> headers, CancellationToken ct = default)
    {
        var response = await httpClient
                             .Request($"{prefix}/{pkg}.{service}/{method}")
                             .WithHeaders(headers: headers)
                             .AllowAnyHttpStatus()
                             .PostJsonAsync(body: data, cancellationToken: ct);

        if (response.StatusCode != 200)
            throw new SfuRPCExceptions(statusCode: response.StatusCode, message: await response.GetStringAsync());
        return await response.GetJsonAsync<TResp>();
    }

    public async ValueTask RequestAsync<TReq>(string                     service, string            method, TReq data,
                                              Dictionary<string, string> headers, CancellationToken ct = default)
    {
        var response = await httpClient
                             .Request($"{prefix}/{pkg}.{service}/{method}")
                             .WithHeaders(headers: headers)
                             .AllowAnyHttpStatus()
                             .PostJsonAsync(body: data, cancellationToken: ct);

        if (response.StatusCode != 200)
            throw new SfuRPCExceptions(statusCode: response.StatusCode, message: await response.GetStringAsync());
    }

    private class SfuRPCExceptions(int statusCode, string message) : Exception;
}