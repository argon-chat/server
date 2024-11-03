// namespace Argon.Api.Controllers;
//
// using Attributes;
// using Grains.Interfaces;
// using Grains.Persistence.States;
// using Microsoft.AspNetCore.Authorization;
// using Microsoft.AspNetCore.Mvc;
// using Sfu;
// using Swashbuckle.AspNetCore.Annotations;
//
// #if DEBUG
//
// public record ServerInputDto(
//     string Name,
//     string Description);
//
// [Route("api/[controller]")]
// public class UsersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
// {
//     [HttpPost]
//     public async Task<IActionResult> Post([FromBody] UserCredentialsInput input)
//     {
//         var userManager = grainFactory.GetGrain<IUserAuthorizationManager>(Guid.NewGuid());
//         await userManager.Register(input);
//         return Ok();
//     }
//
//     [HttpPost("Authorize")]
//     public async Task<ActionResult<JwtToken>> Authorize([FromBody] UserCredentialsInput input)
//     {
//         var userManager = grainFactory.GetGrain<IUserAuthorizationManager>(Guid.NewGuid());
//         var token = await userManager.Authorize(input);
//         return token;
//     }
//
//     [HttpGet("me")]
//     [Authorize]
//     [InjectId]
//     public async Task<ActionResult<UserStorageDto>> Get([SwaggerIgnore] string id)
//     {
//         var userManager = grainFactory.GetGrain<IUserAuthorizationManager>(Guid.NewGuid());
//         return await userManager.GetById(Guid.Parse(id));
//     }
//
//     // [HttpGet("{username}")]
//     // public async Task<ActionResult<UserStorageDto>> GetByUsername(string username)
//     // {
//     //     var userManager = grainFactory.GetGrain<IUserManager>(username);
//     //     return await userManager.Get();
//     // }
//
//     [HttpPost("server")]
//     [Authorize]
//     [InjectId]
//     public async Task<ActionResult<ServerStorage>> CreateServer([SwaggerIgnore] string id,
//         [FromBody] ServerInputDto dto)
//     {
//         var userManager = grainFactory.GetGrain<IUserManager>(Guid.Parse(id));
//         return await userManager.CreateServer(dto.Name, dto.Description);
//     }
//
//     [HttpGet("servers")]
//     [Authorize]
//     [InjectId]
//     public async Task<ActionResult<List<ServerStorage>>> GetServers([SwaggerIgnore] string id)
//     {
//         var userManager = grainFactory.GetGrain<IUserManager>(Guid.Parse(id));
//         return await userManager.GetServers();
//     }
//
//     [HttpGet("servers/{serverId}/channels")]
//     [Authorize]
//     [InjectId]
//     public async Task<ActionResult<IEnumerable<ChannelStorage>>> GetServerChannels([SwaggerIgnore] string id,
//         Guid serverId)
//     {
//         var userManager = grainFactory.GetGrain<IUserManager>(Guid.Parse(id));
//         var channels = await userManager.GetServerChannels(serverId);
//         return channels.ToList();
//     }
//
//     [HttpGet("servers/{serverId}/channels/{channelId}")]
//     [Authorize]
//     [InjectId]
//     public async Task<ActionResult<ChannelStorage>> GetChannel([SwaggerIgnore] string id, Guid serverId, Guid channelId)
//     {
//         var userManager = grainFactory.GetGrain<IUserManager>(Guid.Parse(id));
//         return await userManager.GetChannel(serverId, channelId);
//     }
//
//     [HttpPost("servers/{serverId}/channels/{channelId}/join")]
//     [Authorize]
//     [InjectId]
//     public async Task<ActionResult<RealtimeToken>> JoinChannel([SwaggerIgnore] string id, Guid serverId, Guid channelId)
//     {
//         var userManager = grainFactory.GetGrain<IUserManager>(Guid.Parse(id));
//         return await userManager.JoinChannel(serverId, channelId);
//     }
// }
// #endif

