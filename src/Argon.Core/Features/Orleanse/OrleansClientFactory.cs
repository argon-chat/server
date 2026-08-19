namespace Argon.Features;

using Argon.Features.Clustering.Regions;

using Api.Features.Orleans.Client;
using Clustering;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NatsStreaming;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Services;
using StackExchange.Redis;

public interface IClusterClientFactory
{
    Task<IServiceProvider> CreateClusterClient(string dc, CancellationToken ct = default);
}

public class OrleansClientFactory(IConfiguration configuration, IHostEnvironment env, IServiceProvider provider) : IClusterClientFactory
{
    public async Task<IServiceProvider> CreateClusterClient(string dc, CancellationToken ct = default)
    {
        var services = new ServiceCollection();
        
        services.AddKeyedSingleton("dc", dc);

        services.Add(new ServiceDescriptor(typeof(ILoggerFactory), null,
            (_, _) => provider.GetRequiredService(typeof(ILoggerFactory)),
            ServiceLifetime.Singleton));
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));

        services.Add(new ServiceDescriptor(typeof(IConfiguration), null,
            (_, _) => provider.GetRequiredService(typeof(IConfiguration)),
            ServiceLifetime.Singleton));
        services.Add(new ServiceDescriptor(typeof(IArgonDcRegistry), null,
            (_, _) => provider.GetRequiredService(typeof(IArgonDcRegistry)),
            ServiceLifetime.Singleton));
        services.Add(new ServiceDescriptor(typeof(IHostApplicationLifetime), null,
            (_, _) => provider.GetRequiredService(typeof(IHostApplicationLifetime)),
            ServiceLifetime.Singleton));
        services.Add(new ServiceDescriptor(typeof(NatsContext), null,
            (_, _) => provider.GetRequiredService(typeof(NatsContext)),
            ServiceLifetime.Singleton));

        services.AddOrleansClient(q => Builder(q, env, configuration, dc));


        var typeManifests = provider.GetServices<IConfigureOptions<TypeManifestOptions>>();

        foreach (var manifest in typeManifests)
            services.AddSingleton(manifest);

        return services.BuildServiceProvider(true);
    }

    public static void Builder(IClientBuilder x, IHostEnvironment env, IConfiguration config, string region)
    {
        // Must agree with the silos: same knobs, same defaults.
        var endpoints = ArgonClusterEndpoints.Resolve(config);

        x.Configure<ClusterOptions>(q =>
        {
            q.ClusterId = endpoints.ClusterId;
            q.ServiceId = endpoints.ServiceId;
        });
        x.Configure<GatewayOptions>(options => { options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(10); });
        // Not ClusterClientRetryFilter. That one retries SiloUnavailableException and gives up on
        // everything else, and OutsideRuntimeClient rethrows the moment a filter gives up — which is
        // how an entry point booting while its own gateways are down takes the process with it. The
        // region policy documents the same failure at length; there is no reason the local client
        // should keep the version that has it.
        // OutsideRuntimeClient resolves this from the container, so registering it is enough.
        x.Services.AddSingleton<IClientConnectionRetryFilter>(sp => new RegionConnectionRetryFilter(
            region, TimeSpan.FromSeconds(30),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Argon.ClusterClient")));
        x.Configure<ExceptionSerializationOptions>(q => q.SupportedNamespacePrefixes.Add("Argon"));
        // Redis clustering everywhere; USE_LOCALHOST_CLUSTERING is the local-dev escape for running
        // without a Redis container. Multi-region and its connection observer are not implemented.
        if (Environment.GetEnvironmentVariable("USE_LOCALHOST_CLUSTERING") is not null)
            x.UseLocalhostClustering();
        else
            x.UseRedisClustering(z
                => z.ConfigurationOptions = new RedisProfileRegistry(config).BuildOptions(RedisProfiles.Orleans));
    }
}