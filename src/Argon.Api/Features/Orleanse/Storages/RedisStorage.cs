namespace Argon.Features.Orleanse.Storages;

using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers;
using Orleans.Storage;
using Services;
using StackExchange.Redis;

public class RedisStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly ClusterOptions           _clusterOptions;
    private readonly RedisGrainStorageOptions _options;
    private readonly string                   _storageName;
    private readonly IRedisPoolConnections    _redisPool;

    public RedisStorage(string storageName, ClusterOptions clusterOptions, IOptions<RedisGrainStorageOptions> options, IRedisPoolConnections redisPool)
    {
        _clusterOptions = clusterOptions;
        _redisPool = redisPool;
        _options        = options.Value;
        _storageName    = storageName;
    }

    public RedisStorage(IProviderRuntime providerRuntime, IOptions<RedisGrainStorageOptions> options,
        IOptions<ClusterOptions> clusterOptions, string name, IRedisPoolConnections redisPool)
    {
        _options        = options.Value;
        _clusterOptions = clusterOptions.Value;
        _storageName    = name;
        _redisPool = redisPool;
    }

#region Implementation of ILifecycleParticipant<ISiloLifecycle>

    public void Participate(ISiloLifecycle observer) => observer.Subscribe(OptionFormattingUtilities.Name<RedisStorage>(_storageName),
        ServiceLifecycleStage.ApplicationServices, ct => Task.CompletedTask);

#endregion

    private static string GetKey(GrainId grainId, string stateName) => $"@grains/{grainId.Type}/{grainId.ToString()}:{stateName}";

#region Implementation of IGrainStorage

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        using var scope = _redisPool.Rent();

        var strData = await scope.GetDatabase().StringGetAsync(GetKey(grainId, stateName));
        if (strData.IsNullOrEmpty) return;

        var data = _options.GrainStorageSerializer.Deserialize<T>(strData);
        grainState.State = data;
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        using var scope = _redisPool.Rent();

        var state = _options.GrainStorageSerializer.Serialize(grainState.State);
        await scope.GetDatabase().StringSetAsync(GetKey(grainId, stateName), state.ToString());
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        using var scope = _redisPool.Rent();
        await scope.GetDatabase().KeyDeleteAsync(GetKey(grainId, stateName));
    }

#endregion
}

public class RedisGrainStorageOptions : IStorageProviderSerializerOptions
{
#region Implementation of IStorageProviderSerializerOptions

    public IGrainStorageSerializer GrainStorageSerializer { get; set; }
    public int                     DatabaseName           { get; set; } = 7;

#endregion
}

public static class RedisGrainStorageFactory
{
    internal static IGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<RedisGrainStorageOptions>>();
        var clusterOptions = services.GetProviderClusterOptions(name);

        return ActivatorUtilities.CreateInstance<RedisStorage>(services, Options.Create(optionsMonitor.Get(name)), name, clusterOptions);
    }
}