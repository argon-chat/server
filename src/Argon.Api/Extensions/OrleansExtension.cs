namespace Argon.Api.Extensions;

using System.Diagnostics.CodeAnalysis;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Placement.Repartitioning;
using Orleans.Storage;

internal class MemoryPackStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input) => new(MemoryPackSerializer.Serialize(input));

    public T Deserialize<T>(BinaryData input) => MemoryPackSerializer.Deserialize<T>(input) ?? throw new InvalidOperationException();
}

internal class Zalupus : IImbalanceToleranceRule
{
    public bool IsSatisfiedBy(uint imbalance) => imbalance % 2 == 0;
}

public static class OrleansExtension
{
    [Experimental("ORLEANSEXP001")]
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<ClusterOptions>(cluster =>
            {
                cluster.ClusterId = "Api";
                cluster.ServiceId = "Api";
            }).AddAdoNetGrainStorage("OrleansStorage", options =>
            {
                options.Invariant              = "Npgsql";
                options.ConnectionString       = builder.Configuration.GetConnectionString("DefaultConnection");
                options.GrainStorageSerializer = new MemoryPackStorageSerializer();
            }).AddReminders().AddActivationRepartitioner<Zalupus>().AddMemoryGrainStorage("CacheStorage").UseDashboard(o => o.Port = 22832);
            if (builder.Environment.IsDevelopment()) siloBuilder.UseLocalhostClustering();
            else siloBuilder.UseKubeMembership();
        });

        return builder;
    }
}