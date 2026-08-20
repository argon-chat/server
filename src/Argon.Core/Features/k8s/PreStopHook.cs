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
/// <para>A client role does the same thing with the half of it that applies. It has no activations,
/// so there is nothing to hand over — but it holds every client websocket, and stopping the moment
/// Kubernetes asks severed all of them from a pod that was still in the Service. So it reports
/// not-ready and waits, and the pod leaves the Service before its sockets do. That was the whole gap:
/// the drain service is absent on a client role, and this hook read the absence as "nothing to do".</para>
///
/// <para>The request is answered only once that work is done, and that is the point. Kubernetes holds
/// the pod in <c>Terminating</c> until a preStop hook returns, so blocking here is what buys the
/// drain — or the wait — its time, up to <c>terminationGracePeriodSeconds</c>, which therefore has to
/// be longer than either or the kill arrives mid-handover.</para>
///
/// <para>Loopback only, both paths. One of them ends the process and the other decides whether a silo
/// takes traffic; neither is anyone else's business.</para>
///
/// <para>The second path is the way back, and it is a silo's alone. Every exit from a drain leaves
/// the silo <c>Drained</c>, including the failures — a half-drained silo must not advertise itself as
/// ready — so without something that can say "never mind", the only way to return a silo to service
/// is to redeploy it. <c>/internal/undrain</c> is that something, and it is what makes a cancelled
/// maintenance window recoverable. A client role has no equivalent because it has no state to be
/// recalled from: its stop is a countdown, not a condition.</para>
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

        var outcome = drain is not null
            ? await Drain(context, drain, logger)
            : await LeaveTheService(context, logger);

        logger.LogWarning("Shutdown triggered from internal endpoint. {Outcome}", outcome);

        context.Response.StatusCode = 200;
        await context.Response.WriteAsync($"Shutdown initiated. {outcome}");

        // After the response, so the reply is not lost to the host tearing the server down under it.
        _ = Task.Run(lifetime.StopApplication);
    }

    /// <summary>Hands this silo's activations to the rest of the cluster.</summary>
    private async static Task<string> Drain(HttpContext context, ISiloDrainService drain, ILogger logger)
    {
        logger.LogWarning("Shutdown requested; draining before stopping.");

        try
        {
            return (await drain.StartDrainingAsync(context.RequestAborted)).Message;
        }
        catch (Exception e)
        {
            // A drain that fails is not a reason to refuse to stop: Kubernetes is taking this pod
            // away either way, and the only choice left is whether the shutdown is orderly.
            logger.LogError(e, "Drain failed; stopping anyway");
            return $"drain failed: {e.Message}";
        }
    }

    /// <summary>
    /// A client role's whole graceful stop: say not-ready, then wait to be believed.
    /// </summary>
    /// <remarks>
    /// <para>There is nothing to hand over — no activations live here — but there are the client
    /// websockets, and an entry point attaches a session grain to each one. Stopping the instant
    /// Kubernetes asks severs every one of them, and severs them from a pod that is still in the
    /// Service, so the reconnect can land right back on it. The wait is what puts the removal from
    /// the Service before the process ends rather than after.</para>
    ///
    /// <para>It waits on the clock rather than on a signal because there is no signal to wait for:
    /// nothing tells a pod it has left a Service. The length of it is the deployment's to know, which
    /// is why it is configuration.</para>
    ///
    /// <para>Uncancellable on purpose. If the caller gives up — a <c>curl --max-time</c> that was set
    /// too short — the pod is still on its way out and finishing the wait is still the right thing;
    /// aborting it here would stop the process early, which is exactly the behaviour being fixed.</para>
    /// </remarks>
    private async static Task<string> LeaveTheService(HttpContext context, ILogger logger)
    {
        var stop = context.RequestServices.GetService<ClientStopSignal>();

        // Neither a silo nor a client role — a host that mapped this hook without Argon's clustering.
        if (stop is null)
            return "nothing to hand over and no readiness to withdraw";

        var lead = context.RequestServices
           .GetRequiredService<IOptions<Web.HostHooksOptions>>().Value.PreStopLeadTime;

        // The wait is measured from when readiness was first withdrawn, not from this call. Kubernetes
        // retries a pre-stop hook, and a supervisor script can call it twice; each of those would
        // otherwise start the countdown over and hold the process open for a multiple of the lead time
        // — past the pod's terminationGracePeriod, which turns an orderly stop into a SIGKILL.
        stop.RequestStop();

        var withdrawnAt = stop.RequestedAt ?? DateTimeOffset.UtcNow;
        var elapsed     = DateTimeOffset.UtcNow - withdrawnAt;
        var remaining   = lead - elapsed;

        if (remaining <= TimeSpan.Zero)
            return lead <= TimeSpan.Zero
                ? "not ready; stopping without a wait"
                : $"not ready since {withdrawnAt:O}; the wait is already served";

        logger.LogWarning(
            "Shutdown requested; reporting not-ready and waiting {Remaining} of {Lead} for the service endpoints.",
            remaining, lead);

        await Task.Delay(remaining, CancellationToken.None);

        return $"not ready for {lead}, connections should have moved";
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
            await context.Response.WriteAsync(
                "No drain service here; this role hosts no activations. A client role's stop is a " +
                "countdown to exit rather than a state, so there is nothing to cancel.");
            return;
        }

        var result = drain.CancelDraining();

        logger.LogWarning("Undrain requested from the internal endpoint. {Outcome}", result.Message);

        context.Response.StatusCode = result.IsSuccess ? 200 : 409;
        await context.Response.WriteAsync(result.Message);
    }
}
