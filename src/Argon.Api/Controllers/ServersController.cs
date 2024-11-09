namespace Argon.Api.Controllers;
#if DEBUG
using Attributes;
using global::Grains.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Models;
using Swashbuckle.AspNetCore.Annotations;

[Route("api/[controller]")]
public class ServersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
{
    [HttpPost, Authorize, InjectId]
    public async Task<ActionResult<ServerDto>> Post([SwaggerIgnore] string id, [FromBody] ServerInput input)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.NewGuid());
        return await serverManager.CreateServer(input, Guid.Parse(id));
    }

    [HttpGet("{serverId:guid}"), Authorize]
    public async Task<ActionResult<ServerDto>> Get(Guid serverId)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(serverId);
        return await serverManager.GetServer();
    }

    [HttpPatch("{serverId:guid}"), Authorize]
    public async Task<ActionResult<ServerDto>> Patch(Guid serverId, [FromBody] ServerInput input)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(serverId);
        return await serverManager.UpdateServer(input);
    }

    [HttpDelete("{serverId:guid}"), Authorize]
    public async Task<ActionResult> Delete(Guid serverId)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(serverId);
        await serverManager.DeleteServer();
        return Ok();
    }
}
#endif