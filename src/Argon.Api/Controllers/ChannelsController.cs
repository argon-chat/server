namespace Argon.Api.Controllers;

#if DEBUG
using Attributes;
using Entities;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sfu;
using Swashbuckle.AspNetCore.Annotations;

[Authorize, Route("api/[controller]/{channelId:guid}")]
public class ChannelsController(IGrainFactory grainFactory) : ControllerBase
{
    [HttpPost, Route("join"), InjectId]
    public async Task<RealtimeToken> Join([SwaggerIgnore] string id, Guid channelId) =>
        await grainFactory.GetGrain<IChannelGrain>(channelId).Join(Guid.Parse(id));

    [HttpPost, Route("leave"), InjectId]
    public async Task Leave([SwaggerIgnore] string id, Guid channelId) =>
        await grainFactory.GetGrain<IChannelGrain>(channelId).Leave(Guid.Parse(id));

    [HttpGet, Route("")]
    public async Task<ChannelDto> GetChannel(Guid channelId) => await grainFactory.GetGrain<IChannelGrain>(channelId).GetChannel();

    [HttpPut, HttpPatch, Route("")]
    public async Task<ChannelDto> UpdateChannel(Guid channelId, [FromBody] ChannelInput input) =>
        await grainFactory.GetGrain<IChannelGrain>(channelId).UpdateChannel(input);
}
#endif