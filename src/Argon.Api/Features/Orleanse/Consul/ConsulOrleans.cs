namespace Argon.Api.Features.Orleans.Consul;

using global::Consul;
using global::Orleans.Messaging;
using global::Orleans.Runtime.Hosting;
using global::Orleans.Runtime.Membership;

public static class ConsulOrleans
{
    public static WebApplicationBuilder AddConsul(this WebApplicationBuilder builder)
    {
        
        builder.Services.AddSingleton<IConsulClient>(q => new ConsulClient());
        return builder;
    }

    public static IClientBuilder AddConsulClustering(this IClientBuilder builder)
        => builder.ConfigureServices(services => services.AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>());
    public static ISiloBuilder AddConsulClustering(this ISiloBuilder builder)
        => builder.ConfigureServices(services => services.AddSingleton<IMembershipTable, ConsulMembership>());

    public static ISiloBuilder AddConsulGrainDirectory(this ISiloBuilder builder, string name)
    {
        builder.Configure<ConsulDirectoryOptions>(q => { q.Directory = name; });
        builder.ConfigureServices(q => { q.AddSingleton<ConsulDirectory>(); });

        return builder.AddGrainDirectory(name, (q, w) => q.GetRequiredService<ConsulDirectory>());
    }
}