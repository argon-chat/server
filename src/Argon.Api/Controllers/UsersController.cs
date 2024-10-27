namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Grains.Persistence.States;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sfu;

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

    [HttpPost("server")]
    [Authorize]
    public async Task<ActionResult<ServerStorage>> CreateServer([FromBody] string name, string description)
    {
        var username = User.Claims.FirstOrDefault(cl => cl.Type == "username")?.Value;
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.CreateServer(name, description);
    }

    [HttpGet("servers")]
    [Authorize]
    public async Task<ActionResult<List<ServerStorage>>> GetServers()
    {
        var username = User.Claims.FirstOrDefault(cl => cl.Type == "username")?.Value;
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.GetServers();
    }

    [HttpGet("servers/{serverId}/channels")]
    [Authorize]
    public async Task<ActionResult<IEnumerable<ChannelStorage>>> GetServerChannels(Guid serverId)
    {
        var username = User.Claims.FirstOrDefault(cl => cl.Type == "username")?.Value;
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        var channels = await userManager.GetServerChannels(serverId);
        return channels.ToList();
    }

    [HttpGet("servers/{serverId}/channels/{channelId}")]
    [Authorize]
    public async Task<ActionResult<ChannelStorage>> GetChannel(Guid serverId, Guid channelId)
    {
        var username = User.Claims.FirstOrDefault(cl => cl.Type == "username")?.Value;
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.GetChannel(serverId, channelId);
    }

    [HttpPost("servers/{serverId}/channels/{channelId}/join")]
    [Authorize]
    public async Task<ActionResult<RealtimeToken>> JoinChannel(Guid serverId, Guid channelId)
    {
        var username = User.Claims.FirstOrDefault(cl => cl.Type == "username")?.Value;
        var userManager = grainFactory.GetGrain<IUserManager>(username);
        return await userManager.JoinChannel(serverId, channelId);
    }
}
#endif