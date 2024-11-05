namespace Argon.Api.Controllers;
#if DEBUG
using Attributes;
using Entities;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

[Route(template: "api/[controller]")]
public class ServersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
{
    [HttpPost, Authorize, InjectId]
    public async Task<ActionResult<ServerDto>> Post([SwaggerIgnore] string id, [FromBody] ServerInput input)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(primaryKey: Guid.NewGuid());
        return await serverManager.CreateServer(input: input, creatorId: Guid.Parse(input: id));
    }

    [HttpGet(template: "{serverId:guid}"), Authorize]
    public async Task<ActionResult<ServerDto>> Get(Guid serverId)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(primaryKey: serverId);
        return await serverManager.GetServer();
    }

    [HttpPatch(template: "{serverId:guid}"), Authorize]
    public async Task<ActionResult<ServerDto>> Patch(Guid serverId, [FromBody] ServerInput input)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(primaryKey: serverId);
        return await serverManager.UpdateServer(input: input);
    }

    [HttpDelete(template: "{serverId:guid}"), Authorize]
    public async Task<ActionResult> Delete(Guid serverId)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(primaryKey: serverId);
        await serverManager.DeleteServer();
        return Ok();
    }
}
#endif