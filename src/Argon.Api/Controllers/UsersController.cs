namespace Argon.Api.Controllers;

#if DEBUG
using Attributes;
using Entities;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

[Route(template: "api/[controller]")]
public class UsersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<UserDto>> Post([FromBody] UserCredentialsInput input)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(primaryKey: Guid.NewGuid());
        return await userManager.CreateUser(input: input);
    }

    [HttpPost(template: "Authorize")]
    public async Task<ActionResult<JwtToken>> Authorize([FromBody] UserCredentialsInput input)
    {
        var userManager = grainFactory.GetGrain<ISessionManager>(primaryKey: Guid.NewGuid());
        return await userManager.Authorize(input: input);
    }

    [HttpGet(template: "Me"), Authorize, InjectId]
    public async Task<ActionResult<UserDto>> Get([SwaggerIgnore] string id)
    {
        var userManager = grainFactory.GetGrain<ISessionManager>(primaryKey: Guid.Parse(input: id));
        return await userManager.GetUser();
    }
}
#endif