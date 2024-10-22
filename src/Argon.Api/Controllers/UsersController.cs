namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Grains.Persistence.States;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

#if DEBUG
public record UserInputDto(string Username, string Password);

[Route("api/[controller]")]
public class UsersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<UserStorageDto>> Post([FromBody] UserInputDto dto)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(dto.Username);
        return await userManager.Create(dto.Password);
    }

    [HttpPost("authenticate")]
    public async Task<ActionResult<string>> Authenticate([FromBody] UserInputDto dto)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(dto.Username);
        var token = await userManager.Authenticate(dto.Password);
        return token;
    }

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<UserStorageDto>> Get()
    {
        var username = User.Claims.FirstOrDefault(cl => cl.Type == "username")?.Value;
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.Get();
    }

    [HttpGet("{username}")]
    [Authorize]
    public async Task<ActionResult<UserStorageDto>> Get(string username)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.Get();
    }
}
#endif