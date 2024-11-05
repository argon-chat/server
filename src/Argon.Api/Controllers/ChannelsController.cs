namespace Argon.Api.Controllers;

#if DEBUG
using Attributes;
using Entities;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sfu;
using Swashbuckle.AspNetCore.Annotations;

[Authorize]
[Route("api/[controller]")]
public class ChannelsController(
    IGrainFactory grainFactory
) : ControllerBase
{
    [HttpPost]
    [Route("{channelId:guid}/join")]
    [InjectId]
    public async Task<RealtimeToken> Join([SwaggerIgnore] string id, Guid channelId)
    {
        return await grainFactory.GetGrain<IChannelManager>(channelId).Join(Guid.Parse(id));
    }

    [HttpPost]
    [Route("{channelId:guid}/leave")]
    [InjectId]
    public async Task Leave([SwaggerIgnore] string id, Guid channelId)
    {
        await grainFactory.GetGrain<IChannelManager>(channelId).Leave(Guid.Parse(id));
    }

    [HttpGet]
    [Route("{channelId:guid}")]
    public async Task<ChannelDto> GetChannel(Guid channelId)
    {
        return await grainFactory.GetGrain<IChannelManager>(channelId).GetChannel();
    }

    [HttpPut]
    [HttpPatch]
    [Route("{channelId:guid}")]
    public async Task<ChannelDto> UpdateChannel(Guid channelId, [FromBody] ChannelInput input)
    {
        return await grainFactory.GetGrain<IChannelManager>(channelId).UpdateChannel(input);
    }
}
#endif