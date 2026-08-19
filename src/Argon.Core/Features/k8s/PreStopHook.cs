namespace Argon.Features.k8s;

using Argon.Drains;

/// <summary>
/// The endpoint Kubernetes calls before it takes the pod away.
/// </summary>
/// <remarks>
/// <para>It used to stop the application and nothing else, which on a silo means every activation it
/// held dies where it stands — including the calls in progress. Now it drains first: the silo marks
/// itself unready, hands its activations to the rest of the cluster, waits for them to go, and only
/// then stops.</para>
///
/// <para>The request is answered only once the drain is done, and that is the point. Kubernetes holds
/// the pod in <c>Terminating</c> until a preStop hook returns, so blocking here is what buys the
/// drain its time — up to <c>terminationGracePeriodSeconds</c>, which therefore has to be longer than
/// a drain takes or the kill arrives mid-handover.</para>
///
/// <para>Loopback only. The one thing this endpoint does is end the process, and nothing outside the
/// pod has any business asking for that.</para>
/// </remarks>
public static class PreStopHookExtensions
{
    public const string Path = "/internal/shutdown";

    public static void UsePreStopHook(this WebApplication app)
        => app.Use(Middleware);

    private async static Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path != Path || context.Request.Method != "GET")
        {
            await next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress;

        if (ip is null || !(IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Parse("::1"))))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync($"Forbidden, {ip} denied");
            return;
        }

        var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        var logger   = context.RequestServices.GetRequiredService<ILogger<IHostApplicationLifetime>>();

        // Absent on a client role, which hosts no activations and has nothing to hand over.
        var drain = context.RequestServices.GetService<ISiloDrainService>();

        var outcome = "no grains to drain";

        if (drain is not null)
        {
            logger.LogWarning("Shutdown requested; draining before stopping.");

            try
            {
                outcome = (await drain.StartDrainingAsync(context.RequestAborted)).Message;
            }
            catch (Exception e)
            {
                // A drain that fails is not a reason to refuse to stop: Kubernetes is taking this pod
                // away either way, and the only choice left is whether the shutdown is orderly.
                logger.LogError(e, "Drain failed; stopping anyway");
                outcome = $"drain failed: {e.Message}";
            }
        }

        logger.LogWarning("Shutdown triggered from internal endpoint. {Outcome}", outcome);

        context.Response.StatusCode = 200;
        await context.Response.WriteAsync($"Shutdown initiated. {outcome}");

        // After the response, so the reply is not lost to the host tearing the server down under it.
        _ = Task.Run(lifetime.StopApplication);
    }
}
