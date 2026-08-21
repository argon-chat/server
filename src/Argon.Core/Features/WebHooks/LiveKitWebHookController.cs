namespace Argon.Api.Features.WebHooks;

using Google.Protobuf;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.AspNetCore.Mvc;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class LiveKitWebHookController(ILogger<LiveKitWebHookController> logger, IClusterClient client) : ControllerBase
{
    // WebhookReceiver.Receive ignores its own 'ignoreUnknownFields' flag (it calls MessageParser.WithDiscardUnknownFields,
    // which is binary-only and returns a new parser that the sdk discards), so any field livekit-server adds after the
    // protocol version the sdk was built against throws InvalidProtocolBufferException. Parse leniently ourselves instead.
    private static readonly JsonParser lenientParser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    [HttpPost("/webhook-endpoint")]
    public async Task<IActionResult> Webhook([FromServices] WebhookReceiver webhookReceiver)
    {
        using var reader   = new StreamReader(Request.Body, Encoding.UTF8);
        var       postData = await reader.ReadToEndAsync();

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            logger.LogWarning($"No Authorization header for webhook, return 401");
            return Unauthorized();
        }

        WebhookEvent webhookEvent;
        try
        {
            var claims = webhookReceiver.Verify(authHeader!);
            var hash   = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(postData)));
            if (!string.Equals(claims.Sha256, hash, StringComparison.Ordinal))
            {
                logger.LogWarning("Sha256 checksum of webhook body does not match, return 401");
                return Unauthorized();
            }

            webhookEvent = lenientParser.Parse<WebhookEvent>(postData);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed process livekit webhook payload");
            return Unauthorized();
        }

        logger.LogWarning("Received #{WebhookEventId} {WebhookEventEvent} at {WebhookEventCreatedAt}", webhookEvent.Id, webhookEvent.Event,
            webhookEvent.CreatedAt);

        if (webhookEvent.Event.Equals("participant_joined"))
        {
            var (spaceId, channelId, userId) = ParseRoomParticipant(webhookEvent);
            if (spaceId.HasValue && channelId.HasValue && userId.HasValue)
            {
                await client.GetGrain<IChannelGrain>(channelId.Value)
                    .OnParticipantJoined(userId.Value);
            }
        }
        else if (webhookEvent.Event.Equals("participant_left") || webhookEvent.Event.Equals("participant_connection_aborted"))
        {
            var (_, channelId, userId) = ParseRoomParticipant(webhookEvent);
            if (channelId.HasValue && userId.HasValue)
            {
                await client.GetGrain<IChannelGrain>(channelId.Value).Leave(userId.Value);
            }
            else
                logger.LogInformation("Received {Event}, but channelId or userId not valid format", webhookEvent.Event);
        }
        else if (webhookEvent.Event.Equals("room_finished"))
        {
            var channelId = string.Join("", webhookEvent.Room.Name.Skip(37).Take(36));
            if (Guid.TryParse(channelId, out var chId))
                await client.GetGrain<IChannelGrain>(chId).ClearChannel();
            else
                logger.LogInformation("Received room_finished, but channelId not valid format: {ChannelId}", channelId);
        }

        return Ok();
    }

    private static (Guid? SpaceId, Guid? ChannelId, Guid? UserId) ParseRoomParticipant(WebhookEvent ev)
    {
        var roomName  = ev.Room?.Name ?? "";
        var identity  = ev.Participant?.Identity ?? "";

        var spaceStr   = roomName.Length >= 36 ? roomName[..36] : "";
        var channelStr = roomName.Length >= 73 ? roomName.Substring(37, 36) : "";

        Guid? spaceId   = Guid.TryParse(spaceStr, out var s) ? s : null;
        Guid? channelId = Guid.TryParse(channelStr, out var c) ? c : null;
        Guid? userId    = Guid.TryParse(identity, out var u) ? u : null;

        return (spaceId, channelId, userId);
    }
}