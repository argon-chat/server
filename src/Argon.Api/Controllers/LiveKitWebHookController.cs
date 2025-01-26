namespace Argon.Controllers;

using Microsoft.AspNetCore.Mvc;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class LiveKitWebHookController(ILogger<LiveKitWebHookController> logger) : ControllerBase
{
    [HttpPost("/webhook-endpoint")]
    public IActionResult Webhook([FromBody] LiveKit.Proto.WebhookEvent webhookEvent)
    {
        logger.LogInformation("Called livekit webhook, event: {webhookEvent}, id: {webhookEventId}", webhookEvent.Event, webhookEvent.Id);
        return Ok();
    }
}