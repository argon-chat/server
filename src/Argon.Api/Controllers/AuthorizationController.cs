namespace Argon.Api.Controllers;

using Contracts;
using Extensions;
using Grains.Interfaces;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class AuthorizationController(IGrainFactory grainFactory) : ControllerBase
{
    [HttpPost("/api/auth")]
    public async Task<IActionResult> AuthorizeAsync([FromBody] UserCredentialsInput input)
    {
        var clientName = HttpContext.GetClientName();
        var ipAddress  = HttpContext.GetIpAddress();
        var region     = HttpContext.GetRegion();
        var hostName   = HttpContext.GetHostName();

    #if !DEBUG
        if (string.IsNullOrEmpty(machineKey))
            return BadRequest();
    #endif

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        var result = await grainFactory.GetGrain<IAuthorizationGrain>(IAuthorizationGrain.DefaultId).Authorize(input, connInfo);
        return Ok(result);
    }

    [HttpPost("/api/register")]
    public async Task<IActionResult> RegistrationAsync([FromBody] NewUserCredentialsInput input)
    {
        var clientName = HttpContext.GetClientName();
        var ipAddress  = HttpContext.GetIpAddress();
        var region     = HttpContext.GetRegion();
        var hostName   = HttpContext.GetHostName();

    #if !DEBUG
        if (string.IsNullOrEmpty(machineKey))
            return BadRequest();
    #endif

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        var result = await grainFactory.GetGrain<IAuthorizationGrain>(IAuthorizationGrain.DefaultId).Register(input, connInfo);
        return Ok(result);
    }
}