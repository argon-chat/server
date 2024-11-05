namespace Argon.Api.Controllers;

#if DEBUG
using Entities;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Swashbuckle.AspNetCore.Annotations;

[AttributeUsage(AttributeTargets.Parameter)]
public class InjectFromJwtAttribute : Attribute, IBindingSourceMetadata
{
    public BindingSource BindingSource => BindingSource.Custom;
}

public class InjectFromJwtModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var id = bindingContext.HttpContext.User.Claims.FirstOrDefault(cl => cl.Type == "id")?.Value;
        bindingContext.Result =
            !string.IsNullOrWhiteSpace(id) ? ModelBindingResult.Success(id) : ModelBindingResult.Failed();
        return Task.CompletedTask;
    }
}

[Route("api/[controller]")]
[ApiController]
public class UsersController(IGrainFactory grainFactory, ILogger<UsersController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<UserDto>> Post([FromBody] UserCredentialsInput input)
    {
        var userManager = grainFactory.GetGrain<IUserManager>(Guid.NewGuid());
        return await userManager.CreateUser(input);
    }

    [HttpPost("Authorize")]
    public async Task<ActionResult<JwtToken>> Authorize([FromBody] UserCredentialsInput input)
    {
        var userManager = grainFactory.GetGrain<ISessionManager>(Guid.NewGuid());
        return await userManager.Authorize(input);
    }

    [HttpGet("Me")]
    [Authorize]
    // [InjectId]
    public async Task<ActionResult<UserDto>> Get([SwaggerIgnore] [InjectFromJwt] string id)
    {
        var userManager = grainFactory.GetGrain<ISessionManager>(Guid.Parse(id));
        return await userManager.GetUser();
    }
}
#endif