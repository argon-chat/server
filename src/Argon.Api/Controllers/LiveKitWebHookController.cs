namespace Argon.Controllers;

using Microsoft.AspNetCore.Mvc;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class LiveKitWebHookController(ILogger<LiveKitWebHookController> logger) : ControllerBase
{
    [HttpPost("/webhook-endpoint")]
    public IActionResult Webhook([FromBody] LiveKit.Proto.WebhookEvent webhookEvent)
    {
        logger.LogInformation("Called livekit webhook, event: {webhookEvent}, id: {webhookEventId}, {source}, {auth}",
            webhookEvent.Event, webhookEvent.Id, webhookEvent.ToString(), this.Request.Headers.Authorization.ToString());
        return Ok();
    }
}