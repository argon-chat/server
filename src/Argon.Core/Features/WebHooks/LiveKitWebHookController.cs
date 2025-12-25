namespace Argon.Api.Features.WebHooks;

using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class LiveKitWebHookController(ILogger<LiveKitWebHookController> logger, IClusterClient client) : ControllerBase
{
    [HttpPost("/webhook-endpoint")]
    public async Task<IActionResult> Webhook([FromBody] JToken webhookEvent)
    {
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