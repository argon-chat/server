namespace Argon.Api.Extensions;

using MemoryPack;
using Orleans.Configuration;
using Orleans.Storage;

internal class MemoryPackStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input)
        => new(MemoryPackSerializer.Serialize(input));

    public T Deserialize<T>(BinaryData input)
        => MemoryPackSerializer.Deserialize<T>(input) ?? throw new InvalidOperationException();
}

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .Configure<ClusterOptions>(cluster =>
                {
                    cluster.ClusterId = "Api";
                    cluster.ServiceId = "Api";
                })
                .AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant = "Npgsql";
                    options.ConnectionString =
                        builder.Configuration
                               .GetConnectionString("DefaultConnection");
                    options.GrainStorageSerializer =
                        new MemoryPackStorageSerializer();
                })
                .AddMemoryGrainStorageAsDefault()
                .UseLocalhostClustering();
        });

        return builder;
    }
}