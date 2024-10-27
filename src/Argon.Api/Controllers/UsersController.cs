namespace Argon.Api.Controllers;

using Attributes;
using Grains.Interfaces;
using Grains.Persistence.States;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sfu;

#if DEBUG
public record UserInputDto(string Username, string Password);

public record ServerInputDto(
    string Name,
    string Description);

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
    [InjectUsername]
    public async Task<ActionResult<UserStorageDto>> Get(string username)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.Get();
    }

    [HttpGet("{username}")]
    public async Task<ActionResult<UserStorageDto>> GetByUsername(string username)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.Get();
    }

    [HttpPost("server")]
    [Authorize]
    [InjectUsername]
    public async Task<ActionResult<ServerStorage>> CreateServer(string username, [FromBody] ServerInputDto dto)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.CreateServer(dto.Name, dto.Description);
    }

    [HttpGet("servers")]
    [Authorize]
    [InjectUsername]
    public async Task<ActionResult<List<ServerStorage>>> GetServers(string username)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.GetServers();
    }

    [HttpGet("servers/{serverId}/channels")]
    [Authorize]
    [InjectUsername]
    public async Task<ActionResult<IEnumerable<ChannelStorage>>> GetServerChannels(string username, Guid serverId)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        var channels = await userManager.GetServerChannels(serverId);
        return channels.ToList();
    }

    [HttpGet("servers/{serverId}/channels/{channelId}")]
    [Authorize]
    [InjectUsername]
    public async Task<ActionResult<ChannelStorage>> GetChannel(string username, Guid serverId, Guid channelId)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.GetChannel(serverId, channelId);
    }

    [HttpPost("servers/{serverId}/channels/{channelId}/join")]
    [Authorize]
    [InjectUsername]
    public async Task<ActionResult<RealtimeToken>> JoinChannel(string username, Guid serverId, Guid channelId)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.JoinChannel(serverId, channelId);
    }
}
#endif