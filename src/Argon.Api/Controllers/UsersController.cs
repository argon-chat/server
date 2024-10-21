namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Grains.Persistence.States;
using Microsoft.AspNetCore.Mvc;

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
}