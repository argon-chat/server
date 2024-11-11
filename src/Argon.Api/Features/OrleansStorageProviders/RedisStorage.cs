namespace Argon.Api.Features.OrleansStorageProviders;

using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Storage;
using StackExchange.Redis;

public class RedisStorage(
    string storageName,
    ClusterOptions clusterOptions,
    IOptions<RedisGrainStorageOptions> options,
    IConnectionMultiplexer connectionMux) : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly RedisGrainStorageOptions _options = options.Value;

#region Implementation of ILifecycleParticipant<ISiloLifecycle>

    public void Participate(ISiloLifecycle observer) => observer.Subscribe(OptionFormattingUtilities.Name<RedisStorage>(storageName),
        ServiceLifecycleStage.ApplicationServices, ct => Task.CompletedTask);

#endregion

#region Implementation of IGrainStorage

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => throw new NotImplementedException();

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => throw new NotImplementedException();

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => throw new NotImplementedException();

#endregion
}

public class RedisGrainStorageOptions : IStorageProviderSerializerOptions
{
#region Implementation of IStorageProviderSerializerOptions

    public IGrainStorageSerializer GrainStorageSerializer { get; set; }

#endregion
}