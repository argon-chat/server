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

    private static string GetKey(GrainId grainId, string stateName) => $"{grainId.ToString()}-{stateName}";

#region Implementation of IGrainStorage

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var strData = await connectionMux.GetDatabase(_options.DatabaseName).StringGetAsync(GetKey(grainId, stateName));
        if (strData.IsNullOrEmpty) return;

        var data = _options.GrainStorageSerializer.Deserialize<T>(strData);
        grainState.State = data;
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var state = _options.GrainStorageSerializer.Serialize(grainState.State);
        await connectionMux.GetDatabase(_options.DatabaseName).StringSetAsync(GetKey(grainId, stateName), state.ToString());
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) =>
        await connectionMux.GetDatabase(_options.DatabaseName).KeyDeleteAsync(GetKey(grainId, stateName));

#endregion
}

public class RedisGrainStorageOptions : IStorageProviderSerializerOptions
{
#region Implementation of IStorageProviderSerializerOptions

    public IGrainStorageSerializer GrainStorageSerializer { get; set; }
    public int                     DatabaseName           { get; set; } = 7;

#endregion
}