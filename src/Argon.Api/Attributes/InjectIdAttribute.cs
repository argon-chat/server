namespace Argon.Api.Attributes;

using Microsoft.AspNetCore.Mvc.Filters;

[AttributeUsage(validOn: AttributeTargets.Method)]
public class InjectIdAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var id = context.HttpContext.User.Claims.FirstOrDefault(predicate: cl => cl.Type == "id")?.Value;
        if (!string.IsNullOrWhiteSpace(value: id))
            context.ActionArguments[key: "id"] = id;
        base.OnActionExecuting(context: context);
    }
}