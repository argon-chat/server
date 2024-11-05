namespace Argon.Api.Extensions;

using MemoryPack;
using Orleans.Configuration;
using Orleans.Storage;

internal class MemoryPackStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input)
        => new(data: MemoryPackSerializer.Serialize(value: input));

    public T Deserialize<T>(BinaryData input)
        => MemoryPackSerializer.Deserialize<T>(buffer: input) ?? throw new InvalidOperationException();
}

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(configureDelegate: siloBuilder =>
                                                   {
                                                       siloBuilder
                                                           .Configure<ClusterOptions>(configureOptions: cluster =>
                                                                                      {
                                                                                          cluster.ClusterId = "Api";
                                                                                          cluster.ServiceId = "Api";
                                                                                      })
                                                           .AddAdoNetGrainStorage(name: "OrleansStorage", configureOptions: options =>
                                                                                  {
                                                                                      options.Invariant = "Npgsql";
                                                                                      options.ConnectionString =
                                                                                          builder.Configuration
                                                                                                 .GetConnectionString(name: "DefaultConnection");
                                                                                      options.GrainStorageSerializer =
                                                                                          new MemoryPackStorageSerializer();
                                                                                  })
                                                           .AddMemoryGrainStorageAsDefault()
                                                           .UseLocalhostClustering();
                                                   });

        return builder;
    }
}