namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Contracts;
using Extensions;
using Microsoft.AspNetCore.Mvc;

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
        if (string.IsNullOrEmpty(machineKey))
            return BadRequest();
#endif

        var connInfo = new UserConnectionInfo(region, ipAddress, clientName, hostName);

        var result = await grainFactory
           .GetGrain<IAuthorizationGrain>(IAuthorizationGrain.DefaultId)
           .Authorize(input, connInfo);
        return Ok(result);
    }
}


