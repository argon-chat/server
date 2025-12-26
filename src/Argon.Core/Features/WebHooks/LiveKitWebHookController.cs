namespace Argon.Api.Features.WebHooks;

using Livekit.Server.Sdk.Dotnet;
using Microsoft.AspNetCore.Mvc;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class LiveKitWebHookController(ILogger<LiveKitWebHookController> logger, IClusterClient client) : ControllerBase
{
    [HttpPost("/webhook-endpoint")]
    public async Task<IActionResult> Webhook([FromServices] WebhookReceiver webhookReceiver)
    {
        using var reader   = new StreamReader(Request.Body, Encoding.UTF8);
        var       postData = await reader.ReadToEndAsync();

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return Unauthorized();

        var webhookEvent = webhookReceiver.Receive(postData, authHeader);
        if (webhookEvent.Event.Equals("participant_left"))
        {
            var userId    = webhookEvent.Participant.Identity;
            var channelId = string.Join("", webhookEvent.Room.Name.Skip(37).Take(36));

            if (Guid.TryParse(channelId, out var chId) && Guid.TryParse(userId, out var usrId))
            {
                await client.GetGrain<IChannelGrain>(chId).Leave(usrId);
            }
            else
                logger.LogInformation("Received participant_left, but channelId or userId not valid format: {ChannelId}, {UserId}", channelId, userId);
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
}