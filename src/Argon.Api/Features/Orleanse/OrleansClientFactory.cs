namespace Argon.Features;

using Api.Features.Orleans.Client;
using Api.Features.Orleans.Consul;
using Consul;
using Env;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NatsStreaming;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Services;

public interface IClusterClientFactory
{
    Task<IServiceProvider> CreateClusterClient(string dc, CancellationToken ct = default);
}

public class OrleansClientFactory(IConfiguration configuration, IHostEnvironment env, IServiceProvider provider) : IClusterClientFactory
{
    public async Task<IServiceProvider> CreateClusterClient(string dc, CancellationToken ct = default)
    {
        var services = new ServiceCollection();
        
        services.UseOrleansMessagePack();
        services.AddSerializer(x => x.AddMessagePackSerializer(null, null, MessagePackSerializer.DefaultOptions));
        services.AddKeyedSingleton("dc", dc);

        services.Add(new ServiceDescriptor(typeof(IConsulClient), null,
            (_, _) => provider.GetRequiredService(typeof(IConsulClient)),
            ServiceLifetime.Singleton));
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
        x.Configure<ClusterOptions>(q =>
        {
            q.ClusterId = "argon-cluster";
            q.ServiceId = $"argon-region-{region}";
        });
        x.Configure<GatewayOptions>(options => { options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(10); });
        x.UseConnectionRetryFilter<ClusterClientRetryFilter>();
        x.Configure<ExceptionSerializationOptions>(q => q.SupportedNamespacePrefixes.Add("Argon"));
        if (env.IsMultiRegion())
            x.AddClusterConnectionStatusObserver<DcClusterConnectionListener>();
        if (!env.IsSingleInstance())
            x.AddConsulClustering();
        else
            x.UseLocalhostClustering();
    }
}