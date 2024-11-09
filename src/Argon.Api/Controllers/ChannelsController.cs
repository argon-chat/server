namespace Argon.Api.Controllers;

using Attributes;
using DataTypes;
using global::Grains.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Models;
using Swashbuckle.AspNetCore.Annotations;

#if DEBUG
[Authorize, Route("api/[controller]/{channelId:guid}")]
public class ChannelsController(IGrainFactory grainFactory) : ControllerBase
{
    [HttpPost, Route("join"), InjectId]
    public async Task<RealtimeToken> Join([SwaggerIgnore] string id, Guid channelId) =>
        await grainFactory.GetGrain<IChannelManager>(channelId).Join(Guid.Parse(id));

    [HttpPost, Route("leave"), InjectId]
    public async Task Leave([SwaggerIgnore] string id, Guid channelId) =>
        await grainFactory.GetGrain<IChannelManager>(channelId).Leave(Guid.Parse(id));

    [HttpGet, Route("")]
    public async Task<ChannelDto> GetChannel(Guid channelId) => await grainFactory.GetGrain<IChannelManager>(channelId).GetChannel();

    [HttpPut, HttpPatch, Route("")]
    public async Task<ChannelDto> UpdateChannel(Guid channelId, [FromBody] ChannelInput input) =>
        await grainFactory.GetGrain<IChannelManager>(channelId).UpdateChannel(input);
}
#endif