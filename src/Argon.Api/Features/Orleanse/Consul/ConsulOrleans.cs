namespace Argon.Api.Features.Orleans.Consul;

using global::Orleans.Messaging;
using global::Orleans.Runtime.Hosting;
using Microsoft.Extensions.DependencyInjection;

public static class ConsulOrleans
{
    public static IClientBuilder AddConsulClustering(this IClientBuilder builder)
        => builder.ConfigureServices(services => services.AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>());
    public static ISiloBuilder AddConsulClustering(this ISiloBuilder builder)
        => builder.ConfigureServices(services =>
        {
            services.Configure<ConsulMembershipOptions>(builder.Configuration.GetSection("Orleans:Membership"));
            services.AddSingleton<IMembershipTable, ConsulMembership>();
        });

    public static ISiloBuilder AddConsulGrainDirectory(this ISiloBuilder builder, string name)
    {
        builder.Configure<ConsulDirectoryOptions>(q => { q.Directory = name; });
        builder.ConfigureServices(q => { q.AddSingleton<ConsulDirectory>(); });

        return builder.AddGrainDirectory(name, (q, w) => q.GetRequiredService<ConsulDirectory>());
    }
}