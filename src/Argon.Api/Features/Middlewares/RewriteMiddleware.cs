namespace Argon.Features.Middlewares;

using System.Text.RegularExpressions;

public class RewriteMiddlewareOptions
{
    public int               ExtendedStatus { get; set; }
    public List<RewritePath> Paths          { get; set; }
}

public class RewritePath
{
    public string Path   { get; set; }
    public string Origin { get; set; }
}

public static class RewriteMiddlewareEx
{
    public static void AddRewrites(this WebApplicationBuilder builder)
        => builder.Services.Configure<RewriteMiddlewareOptions>(builder.Configuration.GetSection("Rewriter"));

    public static void UseRewrites(this WebApplication app)
        => app.UseMiddleware<RewriteMiddleware>();
}

public class RewriteMiddleware(RequestDelegate next, IOptions<RewriteMiddlewareOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Host.Host;
        var path   = context.Request.Path.Value ?? "";

        foreach (var rewritePath in options.Value.Paths.Where(rewritePath => rewritePath.Origin.Equals(origin)))
        {
            if (rewritePath.Path.Equals("*"))
            {
                await next(context);
                return;
            }

            // TODO compile mode
            if (!Regex.IsMatch(path, rewritePath.Path))
            {
                context.Response.StatusCode = options.Value.ExtendedStatus;
                return;
            }
            await next(context);
            return;
        }

        await next(context);
    }
}