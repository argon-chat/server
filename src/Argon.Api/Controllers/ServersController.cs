namespace Argon.Api.Controllers;

using Attributes;
using Entities;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

[Route("api/[controller]")]
public class ServersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
{
    [HttpPost]
    [Authorize]
    [InjectId]
    public async Task<ActionResult<ServerDto>> Post([SwaggerIgnore] string id, [FromBody] ServerInput input)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.NewGuid());
        return await serverManager.CreateServer(input, Guid.Parse(id));
    }

    [HttpGet]
    public async Task<ActionResult<ServerDto>> Get(string id)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.Parse(id));
        return await serverManager.GetServer();
    }

    [HttpPatch]
    [Authorize]
    public async Task<ActionResult<ServerDto>> Patch(string id, [FromBody] ServerInput input)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.Parse(id));
        return await serverManager.UpdateServer(input);
    }

    [HttpDelete]
    [Authorize]
    public async Task<ActionResult> Delete(string id)
    {
        var serverManager = grainFactory.GetGrain<IServerManager>(Guid.Parse(id));
        await serverManager.DeleteServer();
        return Ok();
    }
}