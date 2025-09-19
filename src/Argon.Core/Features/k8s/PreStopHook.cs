namespace Argon.Features.k8s;

using Env;

public static class PreStopHookExtensions
{
    public static void UsePreStopHook(this WebApplication app)
        => app.MapWhen(
            _ => app.Environment.IsWorker() || app.Environment.IsGateway(),
            @internal =>
            {
                @internal.Use(Middleware);
            }
        );

    private async static Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == "/internal/shutdown" && context.Request.Method == "GET")
        {
            var ip       = context.Connection.RemoteIpAddress;
            var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
            var logger   = context.RequestServices.GetRequiredService<ILogger<IHostApplicationLifetime>>();
            if (ip is null || !(IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Parse("::1"))))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync($"Forbidden, {ip} denied");
                return;
            }
            _ = Task.Run(() =>
            {
                logger.LogWarning("Shutdown triggered from internal endpoint.");
                lifetime.StopApplication();
            });

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("Shutdown initiated.");
            return;
        }

        await next(context);
    }
}