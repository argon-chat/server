namespace Argon.Api.Features.Orleans;

using Contracts;
using global::Orleans.Clustering.Kubernetes;
using global::Orleans.Configuration;
using global::Orleans.Serialization;
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
                }).AddAdoNetGrainStorage("PubSubStore", options =>
                {
                    options.Invariant        = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                }).AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant              = "Npgsql";
                    options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer = new MemoryPackStorageSerializer();
                }).AddActivationRepartitioner<BalanceRule>().AddStreaming().AddPersistentStreams("default", NatsAdapterFactory.Create, config => { })
               .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, config => { })
            #pragma warning restore ORLEANSEXP001
               .AddMemoryGrainStorage("CacheStorage").UseDashboard(o => o.Port = 22832);
            if (builder.Environment.IsDevelopment())
                siloBuilder.UseLocalhostClustering();
            else
                siloBuilder.UseKubeMembership();
        });

        return builder;
    }
}