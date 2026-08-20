namespace ArgonSharedLogicTest;

using Argon.Features.k8s;
using Argon.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// What a client role reports about itself, and when.
/// </summary>
/// <remarks>
/// The probes are the only thing standing between a deployment and every websocket on the pod, so
/// what matters here is the not-ready answers rather than the ready one: a check that is optimistic
/// in a state it has not seen yet leaves the pod in the Service while it cannot serve.
/// </remarks>
[TestFixture]
public class ClientLifecycleTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped  => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();
    }

    private static ClusterClientStatus Status() => new(NullLogger<ClusterClientStatus>.Instance);

    private static HealthStatus Readiness(ClusterClientStatus cluster, ClientStopSignal stop)
        => new ClientReadinessHealthCheck(cluster, stop, NullLogger<ClientReadinessHealthCheck>.Instance)
           .CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult().Status;

    private static HealthStatus Startup(ClusterClientStatus cluster)
        => new ClientStartupHealthCheck(cluster)
           .CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult().Status;

    // ── readiness ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kestrel is listening before the Orleans client has connected, so this state is reachable by a
    /// probe and answering it optimistically would put the pod in the Service with no cluster behind
    /// it.
    /// </summary>
    [Test]
    public void A_client_that_has_never_reported_a_gateway_is_still_ready()
    {
        var cluster = Status();

        Assert.Multiple(() =>
        {
            Assert.That(cluster.Gateways, Is.EqualTo(ClusterClientStatus.NotReported));

            // Readiness answers one question — is this pod stopping. Only `core` exposes a gateway,
            // so gating on the count would take every client role out of its Service together on any
            // core blip, including the parts of entrypoint that never touch Orleans.
            Assert.That(Readiness(cluster, new ClientStopSignal(new FakeLifetime())),
                Is.EqualTo(HealthStatus.Healthy));
        });
    }

    /// <summary>
    /// The floor under the observer. Orleans raising no gateway notification at all would otherwise
    /// hold every pod of every client role out of its Service permanently, with nothing to point at —
    /// so a client whose own startup completed counts as connected until a count says otherwise.
    /// </summary>
    [Test]
    public void A_client_whose_startup_completed_is_ready_even_with_no_notification()
    {
        var cluster = Status();
        cluster.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(cluster.Gateways, Is.EqualTo(ClusterClientStatus.NotReported));
            Assert.That(cluster.HasEverConnected, Is.True);
            Assert.That(Readiness(cluster, new ClientStopSignal(new FakeLifetime())),
                Is.EqualTo(HealthStatus.Healthy));
        });
    }

    /// <summary>A reported count wins over the floor, including a reported zero.</summary>
    [Test]
    public void A_reported_zero_beats_a_completed_startup()
    {
        var cluster = Status();
        cluster.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        cluster.NotifyGatewayCountChanged(0, 1, false);

        // What the pod reports about the cluster, not what it does about it: an actual count always
        // wins over the "the client's hosted service completed" floor, so an operator reading
        // /health sees zero rather than an optimistic guess.
        Assert.That(cluster.Gateways, Is.Zero);
        Assert.That(cluster.IsConnected, Is.False);
    }

    [Test]
    public void A_connected_client_is_ready()
    {
        var cluster = Status();
        cluster.NotifyGatewayCountChanged(2, 0, false);

        Assert.That(Readiness(cluster, new ClientStopSignal(new FakeLifetime())),
            Is.EqualTo(HealthStatus.Healthy));
    }

    /// <summary>
    /// The event that matters most, and the earlier of the two Orleans raises: a client with no
    /// gateway does not fail a call, it waits out the response timeout.
    /// </summary>
    [Test]
    public void Losing_the_last_gateway_does_not_take_the_pod_out_of_its_service()
    {
        var cluster = Status();
        cluster.NotifyGatewayCountChanged(2, 0, false);
        cluster.NotifyGatewayCountChanged(0, 2, false);

        Assert.Multiple(() =>
        {
            Assert.That(cluster.IsConnected, Is.False, "the loss is observed");
            Assert.That(Readiness(cluster, new ClientStopSignal(new FakeLifetime())),
                Is.EqualTo(HealthStatus.Healthy), "and is not acted on by removing the pod");
        });
    }

    /// <summary>
    /// The same for a connection declared lost, which is a different event and arrives on its own.
    /// </summary>
    /// <remarks>
    /// Measured, because the codebase has asserted the ordering of these two the wrong way round: on
    /// Orleans 10.2.2 <c>NotifyClusterConnectionLost</c> arrives BEFORE
    /// <c>NotifyGatewayCountChanged(1 -&gt; 0)</c>, not after. Either one alone has to be enough to
    /// mark the client disconnected, which is why this asserts the lone-connection-lost case.
    /// </remarks>
    [Test]
    public void Losing_the_cluster_connection_is_observed_without_a_gateway_change()
    {
        var cluster = Status();
        cluster.NotifyGatewayCountChanged(2, 0, false);
        cluster.NotifyClusterConnectionLost();

        Assert.Multiple(() =>
        {
            Assert.That(cluster.IsConnected, Is.False);
            Assert.That(Readiness(cluster, new ClientStopSignal(new FakeLifetime())),
                Is.EqualTo(HealthStatus.Healthy));
        });
    }

    [Test]
    public void A_recovered_connection_is_ready_again()
    {
        var cluster = Status();
        cluster.NotifyGatewayCountChanged(2, 0, false);
        cluster.NotifyClusterConnectionLost();
        cluster.NotifyGatewayCountChanged(2, 2, connectionRecovered: true);

        Assert.That(Readiness(cluster, new ClientStopSignal(new FakeLifetime())),
            Is.EqualTo(HealthStatus.Healthy));
    }

    // ── the stop ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the pre-stop wait: readiness has to be false while the client is still
    /// perfectly able to serve, or Kubernetes has no reason to take the pod out of the Service before
    /// the process ends.
    /// </summary>
    [Test]
    public void A_requested_stop_makes_a_healthy_client_not_ready()
    {
        var cluster = Status();
        cluster.NotifyGatewayCountChanged(2, 0, false);

        var stop = new ClientStopSignal(new FakeLifetime());
        stop.RequestStop();

        Assert.Multiple(() =>
        {
            Assert.That(cluster.IsConnected, Is.True, "the client is still connected; that is the point");
            Assert.That(Readiness(cluster, stop), Is.EqualTo(HealthStatus.Unhealthy));
        });
    }

    /// <summary>A stop nobody asked this class about — a bare SIGTERM — has to read the same.</summary>
    [Test]
    public void A_host_that_is_stopping_is_not_ready_even_if_nothing_asked_this_signal()
    {
        var cluster = Status();
        cluster.NotifyGatewayCountChanged(2, 0, false);

        var lifetime = new FakeLifetime();
        var stop     = new ClientStopSignal(lifetime);

        Assert.That(Readiness(cluster, stop), Is.EqualTo(HealthStatus.Healthy));

        lifetime.StopApplication();

        Assert.Multiple(() =>
        {
            Assert.That(stop.RequestedAt, Is.Null, "nothing went through the signal");
            Assert.That(stop.IsStopping, Is.True);
            Assert.That(Readiness(cluster, stop), Is.EqualTo(HealthStatus.Unhealthy));
        });
    }

    [Test]
    public void Only_the_first_stop_request_wins()
    {
        var stop = new ClientStopSignal(new FakeLifetime());

        Assert.That(stop.RequestStop(), Is.True);
        var first = stop.RequestedAt;

        Assert.Multiple(() =>
        {
            Assert.That(stop.RequestStop(), Is.False);
            Assert.That(stop.RequestedAt, Is.EqualTo(first), "the second call must not move the clock");
        });
    }

    // ── startup and liveness ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The startup probe is the only one that can end a client pod, and it draws the line at "never
    /// connected" rather than "not connected now" — a pod that reached the cluster once and lost it
    /// is in an outage, and restarting it would sever the sockets it is holding for a cluster that is
    /// coming back.
    /// </summary>
    [Test]
    public void Startup_passes_once_and_stays_passed_through_an_outage()
    {
        var cluster = Status();

        Assert.That(Startup(cluster), Is.EqualTo(HealthStatus.Unhealthy));

        cluster.NotifyGatewayCountChanged(1, 0, false);
        Assert.That(Startup(cluster), Is.EqualTo(HealthStatus.Healthy));

        cluster.NotifyClusterConnectionLost();

        Assert.Multiple(() =>
        {
            Assert.That(Startup(cluster), Is.EqualTo(HealthStatus.Healthy));
            Assert.That(cluster.IsConnected, Is.False);
            // Neither probe reacts to the outage. Startup answers "did this pod ever work", readiness
            // answers "is it stopping", and the outage is reported in the data rather than acted on
            // by removing every client pod from its Service at once.
            Assert.That(Readiness(cluster, new ClientStopSignal(new FakeLifetime())),
                Is.EqualTo(HealthStatus.Healthy));
        });
    }

    /// <summary>
    /// Nothing about the cluster fails liveness, because its only remedy is a restart and a restart
    /// cannot reconnect a client that is already retrying — it can only cost the pod its websockets.
    /// </summary>
    [Test]
    public void Liveness_survives_a_cluster_that_is_gone()
    {
        var cluster = Status();
        cluster.NotifyClusterConnectionLost();

        var result = new ClientLivenessHealthCheck(cluster)
           .CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult();

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
    }
}
