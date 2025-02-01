namespace Argon.Controllers;

using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Orleans.BroadcastChannel;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class LiveKitWebHookController(ILogger<LiveKitWebHookController> logger, IClusterClient client) : ControllerBase
{
    [HttpPost("/webhook-endpoint")]
    public async Task<IActionResult> Webhook([FromBody] JToken webhookEvent)
    {
        logger.LogInformation("Called livekit webhook, event: {json}", webhookEvent.ToString());

        var @event = webhookEvent.ToObject<LiveKitWebhookEvent>();

        if (@event is null)
            throw new Exception($"@event is null");


        if (@event.Event.Equals("participant_left"))
        {
            var userId    = @event.Participant.Identity;
            var channelId = string.Join("", @event.Room.Name.Skip(37).Take(36));

            await client.GetGrain<IChannelGrain>(Guid.Parse(channelId)).Leave(Guid.Parse(userId));
        }
        else if (@event.Event.Equals("room_finished"))
        {
            var channelId = string.Join("", @event.Room.Name.Skip(37).Take(36));
            await client.GetGrain<IChannelGrain>(Guid.Parse(channelId)).ClearChannel();
        }

        return Ok();
    }
}


public class LiveKitWebhookEvent
{
    [JsonProperty("event")]
    public string Event { get; set; }

    [JsonProperty("room")]
    public RoomInfo Room { get; set; }

    [JsonProperty("participant")]
    public ParticipantInfo Participant { get; set; }
}

public class RoomInfo
{
    [JsonProperty("name")]
    public string Name { get; set; }
}

public class ParticipantInfo
{
    [JsonProperty("identity")]
    public string Identity { get; set; }
}