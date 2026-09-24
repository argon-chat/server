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
    ILogger<IVoiceControlGrain> logger,
    HybridCache cache) : Grain, IVoiceControlGrain
{
    public Task<string> IssueAuthorizationTokenAsync(ArgonUserId userId, ArgonRoomId roomId, SfuPermissionKind permission,
        SfuMediaRights rights = SfuMediaRights.All, CancellationToken ct = default)
        => Task.FromResult(CreateJwt(userId, SfuPermission.For(permission, roomId.ToRawRoomId(), rights), settings));

    public async Task<bool> UpdateParticipantRightsAsync(ArgonUserId userId, ArgonRoomId roomId, SfuMediaRights rights,
        CancellationToken ct = default)
    {
        var sources    = SfuPermission.PublishSources(rights);
        var permission = new ParticipantPermission
        {
            CanPublish          = sources.Count > 0,
            CanSubscribe        = rights.HasFlag(SfuMediaRights.Listen),
            CanPublishData      = true,
            CanUpdateMetadata   = true,
            CanSubscribeMetrics = true
        };
        permission.CanPublishSources.AddRange(sources);

        try
        {
            await roomClient.UpdateParticipant(new UpdateParticipantRequest
            {
                Room       = roomId.ToRawRoomId(),
                Identity   = userId.ToRawIdentity(),
                Permission = permission
            });
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to update media rights of '{UserId}' in '{RoomId}'", userId.id, roomId.ToRawRoomId());
            return false;
        }
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
            // Usually the participant already left the room; the roster is cleaned up by the caller either way.
            logger.LogWarning(e, "Failed kick user from '{userId}' from '{roomId}'",
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
                            Bucket    = cfg!.Bucket,
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

    private record CloudflareIceRespItem(string? username, string? credential);

    private record CloudflareIceResp(List<CloudflareIceRespItem> iceServers);

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

    private static string CreateJwt(ArgonUserId identity, VideoGrants permissions, IOptions<CallKitOptions> settings)
        => new AccessToken(settings.Value.Sfu.ClientId, settings.Value.Sfu.Secret)
           .WithIdentity(identity.ToRawIdentity())
           .WithName(identity.ToRawIdentity())
           .WithTtl(TimeSpan.FromHours(2))
           .WithGrants(permissions)
           .ToJwt();

#endregion
}