namespace Argon.Sfu;

using Argon.Sfu.Services;
using Flurl.Http;
using Grpc.Core;
using LiveKit.Proto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Rpc;
using System.IdentityModel.Tokens.Jwt;
using static Rpc.Room;

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
    TwirlRoomServiceClient roomClient,
    TwirlEgressClient egressClient,
    ILogger<IArgonSelectiveForwardingUnit> logger) : IArgonSelectiveForwardingUnit
{
    private static readonly Guid SystemUser = new([2, 26, 77, 5, 231, 16, 198, 72, 164, 29, 136, 207, 134, 192, 33, 33]);

    public ValueTask<string> IssueAuthorizationTokenAsync(ArgonUserId userId, ISfuRoomDescriptor channelId, SfuPermission permission) =>
        new(CreateJwt(channelId, userId, permission, settings));

    public ValueTask<string> IssueAuthorizationTokenForMeetAsync(string userName, ISfuRoomDescriptor channelId, SfuPermission permission) =>
        new(CreateMeetJwt(channelId, userName, permission, settings));

    public ValueTask<string> IssueAuthorizationTokenForMeetAsync(string userName, Guid sharedId, SfuPermission permission) =>
        new(CreateMeetJwt(sharedId, userName, permission, settings));

    public ValueTask<bool> SetMuteParticipantAsync(bool isMuted, ArgonUserId userId, ISfuRoomDescriptor channelId)
        => throw new NotImplementedException();

    public async ValueTask<bool> KickParticipantAsync(ArgonUserId userId, ISfuRoomDescriptor channelId)
    {
        try
        {
            logger.LogInformation("Goto kick '{userId}' from '{roomId}' room", userId.id, channelId.ToRawRoomId());
            await roomClient.RemoveParticipantAsync(new RoomParticipantIdentity
            {
                Identity = userId.ToRawIdentity(),
                Room     = channelId.ToRawRoomId(),
            }, CreateAuthHeader(channelId));
            return true;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed kick user from '{userId}' from '{roomId}'",
                userId.id, channelId.ToRawRoomId());
            return false;
        }
    }

    public async ValueTask<EphemeralChannelInfo> EnsureEphemeralChannelAsync(ISfuRoomDescriptor channelId, uint maxParticipants)
    {
        var result = await roomClient.CreateRoomAsync(new CreateRoomRequest
        {
            Name             = channelId.ToRawRoomId(),
            Metadata         = channelId.ToRawRoomId(),
            MaxParticipants  = maxParticipants,
            DepartureTimeout = 10,
            EmptyTimeout     = 2,
            SyncStreams      = true
        }, CreateAuthHeader(channelId));


        return new EphemeralChannelInfo(channelId, result.Sid, result);
    }

    public async ValueTask<bool> PruneEphemeralChannelAsync(ISfuRoomDescriptor channelId)
    {
        await roomClient.DeleteRoomAsync(new DeleteRoomRequest
        {
            Room = channelId.ToRawRoomId()
        }, CreateAuthHeader(channelId));
        return true;
    }

    public async ValueTask<string> BeginRecordAsync(ISfuRoomDescriptor channelId)
    {
        var cfg = settings.Value.S3;

        var result = await egressClient.StartRoomCompositeEgressAsync(new RoomCompositeEgressRequest
        {
            AudioOnly = false,
            RoomName  = channelId.ToRawRoomId(),
            Preset    = EncodingOptionsPreset.H264720P30,
            Layout    = "grid",
            SegmentOutputs =
            {
                new SegmentedFileOutput
                {
                    S3 = new S3Upload
                    {
                        Bucket    = cfg.Bucket,
                        AccessKey = cfg.AccessKey,
                        Region    = cfg.Region,
                        Endpoint  = cfg.Endpoint,
                        Secret    = cfg.Secret
                    },
                    SegmentDuration = 2,
                    PlaylistName    = "output.m3u8",
                    FilenamePrefix  = $"recordings/{channelId.ToRawRoomId()}"
                }
            }
        });

        return result.EgressId;
    }

    public async ValueTask<bool> StopRecordAsync(ISfuRoomDescriptor channelId, string egressId)
    {
        await egressClient.StopEgressAsync(new StopEgressRequest
        {
            EgressId = egressId
        });
        return true;
    }

    private string CreateSystemToken(ISfuRoomDescriptor channelId) =>
        CreateJwt(channelId, new ArgonUserId(SystemUser), SfuPermission.DefaultSystem, settings);

    private static string CreateJwt(ISfuRoomDescriptor roomName, ArgonUserId identity, SfuPermission permissions,
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

    private static string CreateMeetJwt(ISfuRoomDescriptor roomName, string identity, SfuPermission permissions,
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

    private Metadata CreateAuthHeader(ISfuRoomDescriptor room)
        => new()
        {
            {
                "authorization", $"Bearer {CreateSystemToken(room)}"
            }
        };

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
}