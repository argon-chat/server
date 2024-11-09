namespace Argon.Api.Controllers;

#if DEBUG
using Attributes;
using Contracts.etc;
using global::Grains.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Models;
using Swashbuckle.AspNetCore.Annotations;

[Route("api/[controller]")]
public class UsersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<UserDto>> Post([FromBody] UserCredentialsInput input)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(Guid.NewGuid());
        return await userManager.CreateUser(input);
    }

    [HttpPost("Authorize")]
    public async Task<ActionResult<Either<JwtToken, AuthorizationError>>> Authorize([FromBody] UserCredentialsInput input)
    {
        var userManager = grainFactory.GetGrain<ISessionManager>(Guid.NewGuid());
        return await userManager.Authorize(input);
    }

    [HttpGet("Me"), Authorize, InjectId]
    public async Task<ActionResult<UserDto>> Get([SwaggerIgnore] string id)
    {
        var userManager = grainFactory.GetGrain<ISessionManager>(Guid.Parse(id));
        return await userManager.GetUser();
    }
}
#endif