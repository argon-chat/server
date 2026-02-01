namespace Argon.Api.Features.Orleans.Consul;

using global::Orleans.Messaging;
using global::Orleans.Runtime.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sentry.Protocol;

public static class ConsulOrleans
{
    public static IClientBuilder AddConsulClustering(this IClientBuilder builder)
        => builder.ConfigureServices(services => services.AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>());
    extension(ISiloBuilder builder)
    {
        public ISiloBuilder AddConsulClustering()
        {
            builder.AddStartupTask(async (x, _) => await x.GetRequiredService<ConsulMembership>()
               .RegisterOnShutdownRules(), ServiceLifecycleStage.First);
            return builder.ConfigureServices(services =>
            {
                services.Configure<ConsulMembershipOptions>(builder.Configuration.GetSection("Orleans:Membership"));
                services.AddSingleton<ConsulMembership>();
                services.AddSingleton<IMembershipTable>(x => x.GetRequiredService<ConsulMembership>());
            });
        }

        public ISiloBuilder AddConsulClustering(Action<ConsulMembershipOptions> cfg)
        {
            builder.AddStartupTask(async (x, _) => await x.GetRequiredService<ConsulMembership>()
               .RegisterOnShutdownRules(), ServiceLifecycleStage.First);
            return builder.ConfigureServices(services =>
            {
                services.Configure<ConsulMembershipOptions>(builder.Configuration.GetSection("Orleans:Membership"));
                services.PostConfigure(cfg);
                services.AddSingleton<ConsulMembership>();
                services.AddSingleton<IMembershipTable>(x => x.GetRequiredService<ConsulMembership>());
            });
        }

        public ISiloBuilder AddConsulGrainDirectory(string name)
        {
            builder.Configure<ConsulDirectoryOptions>(q => { q.Directory = name; });
            builder.ConfigureServices(q => { q.AddSingleton<ConsulDirectory>(); });

            return builder.AddGrainDirectory(name, (q, w) => q.GetRequiredService<ConsulDirectory>());
        }
    }
}

