namespace Argon.Sfu;

using System.IdentityModel.Tokens.Jwt;
using Flurl.Http;
using LiveKit.Proto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

#if DEBUG
public class ArgonSfuTestController : ControllerBase
{
    [HttpGet("/sfu/create_channel")]
    public async ValueTask<IActionResult> GetData([FromServices] IArgonSelectiveForwardingUnit sfu, [FromQuery] Guid serverId,
        [FromQuery] Guid channelId) => Ok(await sfu.EnsureEphemeralChannelAsync(new ArgonChannelId(new ArgonServerId(serverId), channelId), 15));

    [HttpPost("/sfu/token")]
    public async ValueTask<IActionResult> GetToken([FromServices] IArgonSelectiveForwardingUnit sfu, [FromBody] ArgonChannelId roomId) =>
        Ok(await sfu.IssueAuthorizationTokenAsync(new ArgonUserId(Guid.NewGuid()), roomId, SfuPermission.DefaultUser));
}
#endif

public class ArgonSelectiveForwardingUnit(
    IOptions<SfuFeatureSettings> settings,
    [FromKeyedServices(SfuFeature.HttpClientKey)]
    IFlurlClient httpClient,
    ILogger<IArgonSelectiveForwardingUnit> logger) : IArgonSelectiveForwardingUnit
{
    private const string pkg    = "livekit";
    private const string prefix = "/twirp";

    private static readonly Guid SystemUser = new([2, 26, 77, 5, 231, 16, 198, 72, 164, 29, 136, 207, 134, 192, 33, 33]);

    public ValueTask<string> IssueAuthorizationTokenAsync(ArgonUserId userId, ArgonChannelId channelId, SfuPermission permission) =>
        new (CreateJwt(channelId, userId, permission, settings));

    public ValueTask<string> IssueAuthorizationTokenForMeetAsync(string userName, ArgonChannelId channelId, SfuPermission permission) =>
        new(CreateMeetJwt(channelId, userName, permission, settings));

    public ValueTask<string> IssueAuthorizationTokenForMeetAsync(string userName, Guid sharedId, SfuPermission permission) =>
        new (CreateMeetJwt(sharedId, userName, permission, settings));

    // TODO check validity
    public ValueTask<bool> SetMuteParticipantAsync(bool isMuted, ArgonUserId userId, ArgonChannelId channelId) =>
        throw new NotImplementedException();

    // TODO
    public async ValueTask<bool> KickParticipantAsync(ArgonUserId userId, ArgonChannelId channelId)
    {
        try
        {
            logger.LogInformation("Goto kick '{userId}' from '{channelId}' channel in '{serverId}' server", userId.id, channelId.channelId, channelId.serverId);
            await RequestAsync("RoomService", "RemoveParticipant", new RoomParticipantIdentity
            {
                Identity = userId.ToRawIdentity(),
                Room     = channelId.ToRawRoomId(),
            }, new Dictionary<string, string>
            {
                {
                    "Authorization", $"Bearer {CreateSystemToken(channelId)}"
                }
            });
            return true;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed kick user from '{userId}' from '{channelId}' channel in '{serverId}' server", 
                userId.id, channelId.channelId, channelId.serverId);
            return false;
        }
    }

    public async ValueTask<EphemeralChannelInfo> EnsureEphemeralChannelAsync(ArgonChannelId channelId, uint maxParticipants)
    {
        var result = await RequestAsync<CreateRoomRequest, Room>("RoomService", "CreateRoom", new CreateRoomRequest
        {
            Name             = channelId.ToRawRoomId(),
            Metadata         = channelId.ToRawRoomId(),
            MaxParticipants  = maxParticipants,
            DepartureTimeout = 10,
            EmptyTimeout     = 2,
            SyncStreams      = true
        }, new Dictionary<string, string>
        {
            {
                "Authorization", $"Bearer {CreateSystemToken(channelId)}"
            }
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
            {
                "Authorization", $"Bearer {CreateSystemToken(channelId)}"
            }
        });
        return true;
    }

    private string CreateSystemToken(ArgonChannelId channelId) =>
        CreateJwt(channelId, new ArgonUserId(SystemUser), SfuPermission.DefaultSystem, settings);

    private static string CreateJwt(ArgonChannelId roomName, ArgonUserId identity, SfuPermission permissions,
        IOptions<SfuFeatureSettings> settings)
    {
        var       now     = DateTime.UtcNow;
        JwtHeader headers = new(new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Value.ClientSecret)), "HS256"));

        JwtPayload payload = new()
        {
            {
                "exp", new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds()
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
                "video", permissions.ToDictionary(roomName)
            }
        };

        JwtSecurityToken token = new(headers, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateMeetJwt(ArgonChannelId roomName, string identity, SfuPermission permissions,
        IOptions<SfuFeatureSettings> settings)
    {
        var       now     = DateTime.UtcNow;
        JwtHeader headers = new(new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Value.ClientSecret)), "HS256"));

        JwtPayload payload = new()
        {
            {
                "exp", new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds()
            },
            {
                "iss", settings.Value.ClientId
            },
            {
                "nbf", 0
            },
            {
                "sub", Guid.NewGuid().ToString()
            },
            {
                "name", identity
            },
            {
                "video", permissions.ToDictionary(roomName)
            }
        };

        JwtSecurityToken token = new(headers, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateMeetJwt(Guid roomName, string identity, SfuPermission permissions,
        IOptions<SfuFeatureSettings> settings)
    {
        var       now     = DateTime.UtcNow;
        JwtHeader headers = new(new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Value.ClientSecret)), "HS256"));

        JwtPayload payload = new()
        {
            {
                "exp", new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds()
            },
            {
                "iss", settings.Value.ClientId
            },
            {
                "nbf", 0
            },
            {
                "sub", Guid.NewGuid().ToString()
            },
            {
                "name", identity
            },
            {
                "video", permissions.ToDictionary(roomName)
            }
        };

        JwtSecurityToken token = new(headers, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async ValueTask<TResp> RequestAsync<TReq, TResp>(string service, string method, TReq data, Dictionary<string, string> headers,
        CancellationToken ct = default)
    {
        var response = await httpClient.Request($"{prefix}/{pkg}.{service}/{method}").WithHeaders(headers).AllowAnyHttpStatus()
           .PostJsonAsync(data, cancellationToken: ct);

        if (response.StatusCode != 200)
            throw new SfuRPCExceptions(response.StatusCode, await response.GetStringAsync());
        return await response.GetJsonAsync<TResp>();
    }

    public async ValueTask RequestAsync<TReq>(string service, string method, TReq data, Dictionary<string, string> headers,
        CancellationToken ct = default)
    {
        var response = await httpClient.Request($"{prefix}/{pkg}.{service}/{method}").WithHeaders(headers).AllowAnyHttpStatus()
           .PostJsonAsync(data, cancellationToken: ct);

        if (response.StatusCode != 200)
            throw new SfuRPCExceptions(response.StatusCode, await response.GetStringAsync());
    }

    private class SfuRPCExceptions(int statusCode, string message) : Exception($"{message}, statusCode: {statusCode}");
}