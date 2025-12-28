namespace Argon.Grains;

using Argon.Core.Grains.Interfaces;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.IdentityModel.Tokens;
using Orleans.Concurrency;
using Sfu;
using System.IdentityModel.Tokens.Jwt;
using Flurl.Http;
using Microsoft.Extensions.Caching.Hybrid;

[StatelessWorker]
public class VoiceControlGrain(
    IOptions<CallKitOptions> settings,
    RoomServiceClient roomClient,
    EgressServiceClient egressClient,
    IngressServiceClient ingressClient,
    SipServiceClient sipClient,
    ILogger<IVoiceControlGrain> logger,
    HybridCache cache) : Grain, IVoiceControlGrain
{
    public async Task<string> IssueAuthorizationTokenAsync(ArgonUserId userId, ArgonRoomId roomId, SfuPermissionKind permission,
        CancellationToken ct = default)
        => CreateJwt(roomId, userId, SfuPermission.For(permission, roomId.ToRawRoomId()), settings);

    public async Task<bool> SetMuteParticipantAsync(bool isMuted, string sid, ArgonUserId userId, ArgonRoomId channelId, CancellationToken ct = default)
    {
        var result = await roomClient.MutePublishedTrack(new MuteRoomTrackRequest()
        {
            Identity = userId.ToRawIdentity(),
            Muted    = isMuted,
            Room     = channelId.ToRawRoomId(),
            TrackSid = sid
        });
        return result.Track.Muted;
    }

    public async Task<bool> KickParticipantAsync(ArgonUserId userId, ArgonRoomId channelId, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("Goto kick '{userId}' from '{roomId}' room", userId.id, channelId.ToRawRoomId());
            await roomClient.RemoveParticipant(new RoomParticipantIdentity()
            {
                Identity = userId.ToRawIdentity(),
                Room     = channelId.ToRawRoomId(),
            });
            return true;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed kick user from '{userId}' from '{roomId}'",
                userId.id, channelId.ToRawRoomId());
            return false;
        }
    }

    public async Task<string> BeginRecordAsync(ArgonRoomId channelId, CancellationToken ct = default)
    {
        try
        {
            var cfg = settings.Value.Sfu.S3;

            var result = await egressClient.StartRoomCompositeEgress(new RoomCompositeEgressRequest()
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
        catch (Twirp.Exception e)
        {
            logger.LogCritical(e, $"failed start recording, {e.Type}-{e.Message}");
            throw;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed start recording");
            throw;
        }
    }

    public async Task<RtcEndpoint> GetRtcEndpointAsync(CancellationToken ct = default)
    {
        var list = new List<IceEndpoint>();

        foreach (var iceCfg in settings.Value.Ices
                    .Where(iceCfg => !string.IsNullOrEmpty(iceCfg.AppId) || !string.IsNullOrEmpty(iceCfg.Token))
                    .Where(iceCfg => iceCfg.Scenario == IceScenario.Cloudflare))
        {
            var credentials = await ConsumeCloudflareCredentials(iceCfg.AppId!, iceCfg.Token!);

            if (credentials is null)
                continue;

            list.AddRange(iceCfg.Urls.Select(iceUrl => new IceEndpoint(iceUrl, credentials.Value.username, credentials.Value.password)));
        }

        return new RtcEndpoint(settings.Value.Sfu.PublicUrl, list);
    }

    public async Task<bool> StopRecordAsync(ArgonRoomId channelId, string egressId, CancellationToken ct = default)
    {
        var egress = await egressClient.StopEgress(new StopEgressRequest()
        {
            EgressId = egressId
        });

        logger.LogInformation($"Voice Channel has completed to record");
        logger.LogInformation($"{egress.EgressId}: {egress.Details}, {egress.Error}({egress.ErrorCode}) - {egress.ManifestLocation} - {egress.RoomName} - {egress.RoomId}");
        foreach (var fl in egress.FileResults)
        {
            logger.LogInformation($"{egress.EgressId}: file: {fl.Location}: {fl.Duration}, {fl.Filename}");
        }

        return true;
    }

    public Task<string> InterlinkCallToPhone(ArgonRoomId roomId, ArgonUserId from, string phoneNumberTo, CancellationToken ct = default)
        => throw new NotImplementedException();

    public async Task<string> InterlinkCallToPhone(ArgonRoomId roomId, ArgonUserId from, ArgonUserId to, CancellationToken ct = default)
    {
        return "";
    }


    private record CloudflareIceRespItem(string? username, string? credential);

    private record CloudflareIceResp(List<CloudflareIceRespItem> iceServers);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expiry, (string Username, string Password) Creds)> _cache
        = new();

    private async ValueTask<(string username, string password)?> ConsumeCloudflareCredentials(string clientId, string secret)
    {
        var result = await cache.GetOrCreateAsync(clientId, async token =>
        {
            var result = await $"https://rtc.live.cloudflare.com/v1/turn/keys/{clientId}/credentials/generate-ice-servers"
               .WithOAuthBearerToken(secret)
               .PostJsonAsync(new
                {
                    ttl = 86400
                }, cancellationToken: token);

            var creds = await result.GetJsonAsync<CloudflareIceResp>();

            return creds.iceServers.FirstOrDefault(x => !string.IsNullOrEmpty(x.username) && !string.IsNullOrEmpty(x.credential));
        }, new HybridCacheEntryOptions()
        {
            Expiration = TimeSpan.FromSeconds(86400 / 2),
        });

        if (result is null or { username: null, credential: null })
            return null;
        return (result.username!, result.credential!);
    }

    #region JWT

    private static string CreateJwt(ArgonRoomId roomName, ArgonUserId identity, VideoGrants permissions,
        IOptions<CallKitOptions> settings)
        => new AccessToken(settings.Value.Sfu.ClientId, settings.Value.Sfu.Secret)
           .WithIdentity(identity.ToRawIdentity())
           .WithName(identity.ToRawIdentity())
           .WithTtl(TimeSpan.FromHours(2))
           .WithGrants(permissions)
           .ToJwt();

    private static string CreateMeetJwt(ArgonRoomId roomName, string identity, SfuPermission permissions,
        IOptions<CallKitOptions> settings)
    {
        throw null;
    }

    private static string CreateMeetJwt(Guid roomName, string identity, SfuPermission permissions,
        IOptions<CallKitOptions> settings)
    {
        throw null;
    }

#endregion
}