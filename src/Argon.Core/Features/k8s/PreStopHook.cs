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
/// <para>Loopback only, both paths. One of them ends the process and the other decides whether a silo
/// takes traffic; neither is anyone else's business.</para>
///
/// <para>The second path is the way back. Every exit from a drain now leaves the silo <c>Drained</c>,
/// including the failures — a half-drained silo must not advertise itself as ready — so without
/// something that can say "never mind", the only way to return a silo to service is to redeploy it.
/// <c>/internal/undrain</c> is that something, and it is what makes a cancelled maintenance window
/// recoverable.</para>
/// </remarks>
public static class PreStopHookExtensions
{
    public const string Path        = "/internal/shutdown";
    public const string UndrainPath = "/internal/undrain";

    public static void UsePreStopHook(this WebApplication app)
        => app.Use(Middleware);

    private async static Task Middleware(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;

        if (context.Request.Method != "GET" || (path != Path && path != UndrainPath))
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

        if (path == UndrainPath)
        {
            await Undrain(context);
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

    /// <summary>Returns a drained or draining silo to service.</summary>
    /// <remarks>
    /// Answers 409 when the silo is not in a state that can be cancelled — already active, or already
    /// shutting down — rather than pretending it worked. An operator retrying a failed undrain wants
    /// to know which of the two it is.
    /// </remarks>
    private async static Task Undrain(HttpContext context)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<IHostApplicationLifetime>>();
        var drain  = context.RequestServices.GetService<ISiloDrainService>();

        if (drain is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("No drain service here; this role hosts no activations.");
            return;
        }

        var result = drain.CancelDraining();

        logger.LogWarning("Undrain requested from the internal endpoint. {Outcome}", result.Message);

        context.Response.StatusCode = result.IsSuccess ? 200 : 409;
        await context.Response.WriteAsync(result.Message);
    }
}
