namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Contracts;
using Microsoft.AspNetCore.Mvc;
using Argon.Features;

[ApiController]
public class AuthorizationController(IGrainFactory grainFactory) : ControllerBase
{
    [HttpPost("/api/auth")]
    public async Task<IActionResult> AuthorizeAsync([FromBody] UserCredentialsInput input)
    {
        var clientName = this.HttpContext.GetClientName();
        var ipAddress  = this.HttpContext.GetIpAddress();
        var region     = this.HttpContext.GetRegion();
        var hostName = this.HttpContext.GetHostName();

#if !DEBUG
        if (string.IsNullOrEmpty(hostName))
            return BadRequest();
#endif

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        var result = await grainFactory
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .Authorize(input, connInfo);
        return Ok(result);
    }

    [HttpPost("/api/register")]
    public async Task<IActionResult> RegistrationAsync([FromBody] NewUserCredentialsInput input)
    {
        var clientName = this.HttpContext.GetClientName();
        var ipAddress  = this.HttpContext.GetIpAddress();
        var region     = this.HttpContext.GetRegion();
        var hostName   = this.HttpContext.GetHostName();

#if !DEBUG
        if (string.IsNullOrEmpty(hostName))
            return BadRequest();
#endif

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        var result = await grainFactory
           .GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .Register(input, connInfo);
        return Ok(result);
    }
}


