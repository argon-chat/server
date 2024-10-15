using System.Reflection;
using Argon.Grains.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Argon.Orleans.Client;

public static class Extensions
{
    public static IHostBuilder UseOrleansClient(this IHostBuilder hostBuilder)
    {
        return hostBuilder
            .UseOrleansClient(config =>
            {
                config.Configure<ClusterOptions>(cluster =>
                {
                    cluster.ClusterId = "Api";
                    cluster.ServiceId = "Api";
                });
                config.UseLocalhostClustering();
            })
            .ConfigureLogging(logging => logging.AddConsole())
            .UseConsoleLifetime();
    }

    public static IClusterClient OrleansClient(this IHost host)
    {
        return host.Services.GetRequiredService<IClusterClient>();
    }

    public static IClusterClient SayHello(this IClusterClient client)
    {
        var hello = client.GetGrain<IHello>(Guid.NewGuid(), "Hello", null);
        hello.DoIt("World");
        return client;
    }
}