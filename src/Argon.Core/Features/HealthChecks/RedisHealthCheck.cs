namespace Argon.HealthChecks;

using Argon.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

/// <summary>
/// One multiplexer per distinct Redis server the profiles name, held for the probes alone.
/// </summary>
/// <remarks>
/// <para>Not the pools. Three of the five profiles are served by a <see cref="RedisConnectionPool"/>
/// and the other two — clustering and the SignalR backplane — by multiplexers their consumers open
/// themselves, so there is no one place that already holds a connection to each server. Opening one
/// here per server, and keeping it, costs one socket per distinct connection string, which in the
/// shipped configuration is two. Renting from a pool for every probe would also count the probe in
/// the pool's own metrics, and a pool that grows because it is being probed is a pool telling a
/// story about the wrong thing.</para>
///
/// <para>A multiplexer whose connect failed is dropped rather than kept, because a
/// <c>Lazy</c> that captured a faulted task would report the same dead socket forever. One that
/// connected and later lost the server is dropped too; StackExchange would reconnect it on its own,
/// but a fresh dial is what makes the next answer a statement about now.</para>
/// </remarks>
public sealed class RedisProbeConnections(RedisProfileRegistry registry) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ConnectionMultiplexer>>> connections = new(StringComparer.Ordinal);

    /// <summary>Every profile with a connection string, pooled or not.</summary>
    public IReadOnlyCollection<string> Profiles => registry.Names;

    public async Task<TimeSpan> PingAsync(string profile, CancellationToken ct)
    {
        var settings         = registry.Resolve(profile);
        var connectionString = settings.ConnectionString!;

        var lazy = connections.GetOrAdd(connectionString,
            static key => new Lazy<Task<ConnectionMultiplexer>>(() => ConnectAsync(key)));

        try
        {
            var multiplexer = await lazy.Value.WaitAsync(ct);

            return await multiplexer.GetDatabase(settings.Database).PingAsync().WaitAsync(ct);
        }
        catch
        {
            if (connections.TryRemove(new KeyValuePair<string, Lazy<Task<ConnectionMultiplexer>>>(connectionString, lazy)))
                _ = DiscardAsync(lazy);

            throw;
        }
    }

    private static Task<ConnectionMultiplexer> ConnectAsync(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.ClientName = "argon-probe";

        return ConnectionMultiplexer.ConnectAsync(options);
    }

    private static async Task DiscardAsync(Lazy<Task<ConnectionMultiplexer>> lazy)
    {
        try
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                await lazy.Value.Result.DisposeAsync();
        }
        catch
        {
            // Already broken; there is nothing to report and nobody to report it to.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in connections.Values)
            await DiscardAsync(lazy);

        connections.Clear();
    }
}

/// <summary>
/// Can this process reach every Redis server its profiles name?
/// </summary>
/// <remarks>
/// Every profile, not just the pooled ones, because a role that cannot reach the clustering profile
/// is a silo that will never become <c>Active</c> and a client role whose gateway list stays empty —
/// and the other probes would report that as a slow join rather than as the connection string it is.
/// Each profile is pinged on its own logical database so the answer is per profile even where several
/// share a server; the failures are listed together, since the point of one report is that a
/// deployment with two wrong profiles learns about both.
/// </remarks>
public sealed class RedisHealthCheck(RedisProbeConnections connections, IOptions<ProbeOptions> options)
    : DependencyHealthCheck(options)
{
    protected override async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        var data     = new Dictionary<string, object>();
        var failures = new List<string>();

        foreach (var profile in connections.Profiles)
        {
            try
            {
                var rtt = await connections.PingAsync(profile, ct);
                data[profile] = $"{rtt.TotalMilliseconds:0.#} ms";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                data[profile] = e.Message;
                failures.Add($"{profile}: {e.Message}");
            }
        }

        if (failures.Count > 0)
            return HealthCheckResult.Unhealthy(string.Join("; ", failures), data: data);

        return HealthCheckResult.Healthy($"{data.Count} Redis profile(s) answered", data);
    }
}
