namespace Argon.Api.Filters;

using Microsoft.AspNetCore.Mvc.Filters;

public class InjectIdFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var username = context.HttpContext.User.Claims.FirstOrDefault(cl => cl.Type == "id")?.Value;
        if (!string.IsNullOrWhiteSpace(username))
            context.ActionArguments["id"] = username;
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}