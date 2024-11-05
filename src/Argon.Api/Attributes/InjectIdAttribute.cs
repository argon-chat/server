namespace Argon.Api.Attributes;

using Microsoft.AspNetCore.Mvc.Filters;

[AttributeUsage(AttributeTargets.Method)]
public class InjectIdAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var id = context.HttpContext.User.Claims.FirstOrDefault(cl => cl.Type == "id")?.Value;
        if (!string.IsNullOrWhiteSpace(id))
            context.ActionArguments["id"] = id;
        base.OnActionExecuting(context);
    }
}