namespace Argon.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Client.Core;
using NATS.Client.JetStream;

/// <summary>
/// Can this process reach NATS, and is JetStream enabled where it lands?
/// </summary>
/// <remarks>
/// <para>Two questions because they fail separately. A NATS server that answers a ping but has no
/// JetStream accepts the connection and refuses every stream, and every stream this product has is a
/// JetStream stream — the realtime event fan-out, the bot gateway, the region's own announcements.
/// A server without it is as unusable as no server, and only the account-info call says so.</para>
///
/// <para>The client connects on first use rather than at registration, so a process that has not
/// published anything yet holds a connection in <c>Closed</c>. That is not a finding; the connect is
/// made here, bounded by the probe's timeout rather than the client's own minute, and its outcome
/// is the answer.</para>
/// </remarks>
public sealed class NatsHealthCheck(
    INatsClient            client,
    INatsJSContext         jetStream,
    IOptions<ProbeOptions> options) : DependencyHealthCheck(options)
{
    protected override async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        await client.ConnectAsync();

        var rtt     = await client.PingAsync(ct);
        var account = await jetStream.GetAccountInfoAsync(ct);

        var connection = client.Connection;
        var server     = connection.ServerInfo;

        return HealthCheckResult.Healthy(
            $"NATS {server?.Version ?? "?"} answered in {rtt.TotalMilliseconds:0.#} ms with JetStream enabled",
            new Dictionary<string, object>
            {
                ["state"]         = connection.ConnectionState.ToString(),
                ["rttMs"]         = Math.Round(rtt.TotalMilliseconds, 1),
                ["server"]        = server?.Name ?? "?",
                ["serverVersion"] = server?.Version ?? "?",
                ["streams"]       = account.Streams,
                ["consumers"]     = account.Consumers
            });
    }
}
