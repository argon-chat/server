//#if DEBUG
namespace Argon.Api.Controllers;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shared.SharedGrains;

[Route("api/[controller]"), ApiController]
public class DevController(IGrainFactory grainFactory) : ControllerBase
{
    [HttpGet("/getMessages/{channelId:guid}")]
    public async Task<ActionResult<List<ArgonMessage>>> GetMessages(Guid channelId, int count, int offset)
        => await grainFactory.GetGrain<IChannelGrain>(channelId).GetMessages(count, offset);

    [HttpGet("getAccessToken/{userId:guid}")]
    public async Task<IActionResult> GetAccessKey(Guid userId)
        => Ok(grainFactory.GetGrain<IAccessTokenGrain>(Guid.Empty).GenerateAccessGuidAsync(userId, DateTime.Now, CancellationToken.None));

    //[HttpPost("{userId:guid}/{channelId:guid}")]
    //public async Task SendMessage(Guid userId, Guid channelId, ArgonMessage message)
    //    => await grainFactory.GetGrain<IChannelGrain>(channelId).SendMessage(message.Text);
}

//#endif