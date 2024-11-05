namespace Argon.Api.Filters;

using Microsoft.AspNetCore.Mvc.Filters;

public class InjectIdFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var id = context.HttpContext.User.Claims.FirstOrDefault(cl => cl.Type == "id")?.Value;
        Console.WriteLine($"Injecting id: {id}");
        if (!string.IsNullOrWhiteSpace(id))
            context.ActionArguments["id"] = id;
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}