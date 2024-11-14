namespace Argon.Api.Features;

using Contracts;
using Extensions;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Serialization;
using OrleansStreamingProviders;

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerializer(x =>
        {
            x.AddMemoryPackSerializer();
        });
        builder.Host.UseOrleans(siloBuilder =>
        {
        #pragma warning disable ORLEANSEXP001
            siloBuilder.Configure<ClusterOptions>(cluster =>
                {
                    cluster.ClusterId = "argonchat";
                    cluster.ServiceId = "argonchat";
                }).AddRedisStorage("PubSubStore", options => options.DatabaseName = 1).AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant              = "Npgsql";
                    options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                }).AddActivationRepartitioner<BalanceRule>().AddStreaming()
               .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
               .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { }).UseDashboard(o => o.Port = 22832);
        #pragma warning restore ORLEANSEXP001

            if (builder.Environment.IsDevelopment())
                siloBuilder.UseLocalhostClustering();
            else
                siloBuilder.UseKubeMembership();
        });

        return builder;
    }
}