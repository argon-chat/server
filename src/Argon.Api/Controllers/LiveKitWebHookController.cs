namespace Argon.Controllers;

using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class LiveKitWebHookController(ILogger<LiveKitWebHookController> logger) : ControllerBase
{
    [HttpPost("/webhook-endpoint")]
    public IActionResult Webhook([FromBody] JToken webhookEvent)
    {
        logger.LogInformation("Called livekit webhook, event: {json}", webhookEvent.ToString());
        return Ok();
    }
}