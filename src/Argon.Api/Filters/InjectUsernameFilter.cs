namespace Argon.Api.Filters;

using Microsoft.AspNetCore.Mvc.Filters;

public class InjectUsernameFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var username = context.HttpContext.User.Claims.FirstOrDefault(cl => cl.Type == "username")?.Value;
        if (!string.IsNullOrWhiteSpace(username))
            context.ActionArguments["username"] = username;
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}