namespace Argon.Api.Controllers;

#if DEBUG
using Attributes;
using Entities;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sfu;
using Swashbuckle.AspNetCore.Annotations;

[Authorize, Route(template: "api/[controller]/{channelId:guid}")]
public class ChannelsController(
    IGrainFactory grainFactory
) : ControllerBase
{
    [HttpPost, Route(template: "join"), InjectId]
    public async Task<RealtimeToken> Join([SwaggerIgnore] string id, Guid channelId)
        => await grainFactory.GetGrain<IChannelManager>(primaryKey: channelId).Join(userId: Guid.Parse(input: id));

    [HttpPost, Route(template: "leave"), InjectId]
    public async Task Leave([SwaggerIgnore] string id, Guid channelId)
    {
        await grainFactory.GetGrain<IChannelManager>(primaryKey: channelId).Leave(userId: Guid.Parse(input: id));
    }

    [HttpGet, Route(template: "")]
    public async Task<ChannelDto> GetChannel(Guid channelId)
        => await grainFactory.GetGrain<IChannelManager>(primaryKey: channelId).GetChannel();

    [HttpPut, HttpPatch, Route(template: "")]
    public async Task<ChannelDto> UpdateChannel(Guid channelId, [FromBody] ChannelInput input)
        => await grainFactory.GetGrain<IChannelManager>(primaryKey: channelId).UpdateChannel(input: input);
}
#endif