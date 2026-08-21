namespace Argon.Api.Features.AdminApi.Diagnostics;

using ConsoleContracts;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using System.Runtime.InteropServices;

public static class HostModeExtensions
{
    extension(WebApplicationBuilder builder)
    {
        public WebApplicationBuilder AddDiagnosticServices()
        {
            builder.Services.AddScoped<RuntimeDiagnosticsService>();
            builder.Services.TryAddScoped<DatabaseDiagnosticsService>();
            builder.Services.TryAddScoped<KubernetesDiagnosticsService>();
            builder.Services.TryAddScoped<NatsDiagnosticsService>();
            builder.Services.TryAddScoped<RedisDiagnosticsService>();
            builder.Services.TryAddScoped<OrleansDiagnosticsService>();
            return builder;
        }
    }
}

public class DatabaseDiagnosticsService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<DatabaseDiagnostics?> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);

            stopwatch.Stop();

            var provider = db.Database.ProviderName ?? "Unknown";

            return new DatabaseDiagnostics(
                provider,
                10,
                0,
                1,
                true,
                stopwatch.ElapsedMilliseconds,
                null
            );
        }
        catch (Exception ex)
        {
            return new DatabaseDiagnostics(
                "CockroachDB/PostgreSQL",
                0,
                0,
                0,
                false,
                null,
                ex.Message
            );
        }
    }
}

public class KubernetesDiagnosticsService
{
    public async Task<KubernetesDiagnostics?> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        try
        {
            if (!KubernetesClientConfiguration.IsInCluster())
                return null;

            var       config = KubernetesClientConfiguration.InClusterConfig();
            using var client = new Kubernetes(config);

            var podName = Environment.GetEnvironmentVariable("HOSTNAME") ?? "unknown";
            var namespaceName = File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/namespace")
                ? await File.ReadAllTextAsync("/var/run/secrets/kubernetes.io/serviceaccount/namespace", ct)
                : "default";

            V1Pod? pod = null;
            try
            {
                pod = await client.CoreV1.ReadNamespacedPodAsync(podName, namespaceName, cancellationToken: ct);
            }
            catch
            {
                // Pod not found or no permissions
            }

            var   nodeName           = pod?.Spec?.NodeName ?? "unknown";
            long? cpuLimitMillicores = null;
            long? memoryLimitMb      = null;

            if (pod?.Spec?.Containers != null && pod.Spec.Containers.Count > 0)
            {
                var container = pod.Spec.Containers[0];

                if (container.Resources?.Limits != null)
                {
                    if (container.Resources.Limits.TryGetValue("cpu", out var cpuLimit))
                    {
                        cpuLimitMillicores = cpuLimit.ToInt64() / 1_000_000;
                    }

                    if (container.Resources.Limits.TryGetValue("memory", out var memLimit))
                    {
                        memoryLimitMb = memLimit.ToInt64() / 1024 / 1024;
                    }
                }
            }

            return new KubernetesDiagnostics(
                namespaceName,
                podName,
                nodeName,
                cpuLimitMillicores,
                memoryLimitMb,
                pod?.Status?.Phase == "Running",
                null
            );
        }
        catch (Exception ex)
        {
            return new KubernetesDiagnostics(
                "unknown",
                "unknown",
                "unknown",
                null,
                null,
                false,
                ex.Message
            );
        }
    }
}

public class NatsDiagnosticsService(IConfiguration configuration)
{
    public Task<NatsDiagnostics?> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        try
        {
            var natsUrl = configuration.GetConnectionString("nats");
            if (string.IsNullOrEmpty(natsUrl))
                return Task.FromResult<NatsDiagnostics?>(null);

            // Note: Actual NATS connection check would require NATS.Client
            // For now, return basic configuration info
            var diagnostics = new NatsDiagnostics(
                true,
                natsUrl,
                0,
                0,
                null
            );

            return Task.FromResult<NatsDiagnostics?>(diagnostics);
        }
        catch (Exception ex)
        {
            return Task.FromResult<NatsDiagnostics?>(new NatsDiagnostics(
                false,
                "unknown",
                0,
                0,
                ex.Message
            ));
        }
    }
}

public class OrleansDiagnosticsService(IGrainFactory? grainFactory = null, ILocalSiloDetails? siloDetails = null)
{
    public async Task<OrleansDiagnostics?> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        if (grainFactory is null || siloDetails is null)
            return null;

        try
        {
            var managementGrain = grainFactory.GetGrain<IManagementGrain>(0);

            var hosts = await managementGrain.GetHosts(onlyActive: true);
            var stats = await managementGrain.GetSimpleGrainStatistics();

            var statsList       = stats?.ToList() ?? [];
            var totalGrains     = statsList.Count;
            var activationCount = statsList.Sum(s => s.ActivationCount);

            return new OrleansDiagnostics(
                siloDetails.ClusterId,
                siloDetails.Name,
                siloDetails.SiloAddress.ToString(),
                "Active",
                hosts.Count,
                totalGrains,
                activationCount,
                true,
                null
            );
        }
        catch (Exception ex)
        {
            return new OrleansDiagnostics(
                siloDetails?.ClusterId ?? "unknown",
                siloDetails?.Name ?? "unknown",
                siloDetails?.SiloAddress.ToString() ?? "unknown",
                "Unknown",
                0,
                0,
                0,
                false,
                ex.Message
            );
        }
    }
}

public class RedisDiagnosticsService
{
    private readonly IConnectionMultiplexer? _redis;

    public RedisDiagnosticsService(IConnectionMultiplexer? redis = null)
    {
        _redis = redis;
    }

    public async Task<RedisDiagnostics?> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        if (_redis is null)
            return null;

        try
        {
            var db        = _redis.GetDatabase();
            var endpoints = _redis.GetEndPoints();

            if (endpoints.Length == 0)
                return null;

            var server  = _redis.GetServer(endpoints[0]);
            var info    = await server.InfoAsync("server");
            var memory  = await server.InfoAsync("memory");
            var clients = await server.InfoAsync("clients");

            var serverInfo  = info.FirstOrDefault(g => g.Key == "Server");
            var memoryInfo  = memory.FirstOrDefault(g => g.Key == "Memory");
            var clientsInfo = clients.FirstOrDefault(g => g.Key == "Clients");

            var serverVersion    = serverInfo?.FirstOrDefault(x => x.Key == "redis_version").Value;
            var usedMemoryBytes  = memoryInfo?.FirstOrDefault(x => x.Key == "used_memory").Value;
            var connectedClients = clientsInfo?.FirstOrDefault(x => x.Key == "connected_clients").Value;

            long? usedMemoryMb = null;
            if (!string.IsNullOrEmpty(usedMemoryBytes) && long.TryParse(usedMemoryBytes, out var memBytes))
                usedMemoryMb = memBytes / 1024 / 1024;

            int? clientsCount = null;
            if (!string.IsNullOrEmpty(connectedClients) && int.TryParse(connectedClients, out var cc))
                clientsCount = cc;

            long? totalKeys = null;
            try
            {
                var dbInfo   = await server.InfoAsync("keyspace");
                var keystats = dbInfo.FirstOrDefault(g => g.Key == "Keyspace");
                var db0Info  = keystats?.FirstOrDefault(x => x.Key.StartsWith("db"));

                if (db0Info.HasValue && !string.IsNullOrEmpty(db0Info.Value.Value))
                {
                    var parts    = db0Info.Value.Value.Split(',');
                    var keysPart = parts.FirstOrDefault(p => p.StartsWith("keys="));
                    if (keysPart != null && long.TryParse(keysPart.Split('=')[1], out var keys))
                        totalKeys = keys;
                }
            }
            catch
            {
                // Ignore keyspace errors
            }

            return new RedisDiagnostics(
                _redis.IsConnected,
                serverVersion,
                usedMemoryMb,
                clientsCount,
                totalKeys,
                null
            );
        }
        catch (Exception ex)
        {
            return new RedisDiagnostics(
                false,
                null,
                null,
                null,
                null,
                ex.Message
            );
        }
    }
}

public class RuntimeDiagnosticsService
{
    private static readonly DateTime ProcessStartTime = DateTime.UtcNow;

    public Task<RuntimeDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var process = Process.GetCurrentProcess();

        ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);

        var usedWorkerThreads         = maxWorkerThreads - workerThreads;
        var usedCompletionPortThreads = maxCompletionPortThreads - completionPortThreads;

        var workingSetMb    = process.WorkingSet64 / 1024 / 1024;
        var gcTotalMemoryMb = GC.GetTotalMemory(false) / 1024 / 1024;

        var diagnostics = new RuntimeDiagnostics(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            workingSetMb,
            gcTotalMemoryMb,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            usedWorkerThreads,
            usedCompletionPortThreads,
            (long)(DateTime.UtcNow - ProcessStartTime).TotalSeconds
        );

        return Task.FromResult(diagnostics);
    }
}