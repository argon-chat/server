namespace Argon.Features;

using Api.Features.Orleans.Client;
using Env;
using Orleans.Configuration;
using Orleans.Serialization;
using StackExchange.Redis;

public static class OrleansClientFactory
{
    public static void Builder(IClientBuilder x, IHostEnvironment env, IConfiguration config)
    {
        x.Configure<ClusterOptions>(q =>
        {
            q.ClusterId = "argon-cluster";
            q.ServiceId = "argon-service";
        });
        x.Configure<GatewayOptions>(options => { options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(10); });
        x.UseConnectionRetryFilter<ClusterClientRetryFilter>();
        x.Configure<ExceptionSerializationOptions>(q => q.SupportedNamespacePrefixes.Add("Argon"));
        if (!env.IsSingleInstance())
            x.UseRedisClustering(z
            => z.ConfigurationOptions = ConfigurationOptions.Parse(config.GetConnectionString("cache")!));
        else
            x.UseLocalhostClustering();
    }
}