namespace Argon.Features.Orleanse.Storages;

using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Providers;
using Orleans.Storage;
using StackExchange.Redis;

public class RedisStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly ClusterOptions           _clusterOptions;
    private readonly ILogger<RedisStorage>    _logger;
    private readonly RedisGrainStorageOptions _options;
    private readonly string                   _storageName;
    private readonly IConnectionMultiplexer   connectionMux;
    private readonly IProviderRuntime         providerRuntime;

    public RedisStorage(string storageName, ClusterOptions clusterOptions, IOptions<RedisGrainStorageOptions> options,
        IConnectionMultiplexer connection)
    {
        _clusterOptions = clusterOptions;
        _options        = options.Value;
        _storageName    = storageName;
        connectionMux   = connection;
    }

    public RedisStorage(ILogger<RedisStorage> logger, IProviderRuntime providerRuntime, IOptions<RedisGrainStorageOptions> options,
        IOptions<ClusterOptions> clusterOptions, string name, IConnectionMultiplexer connection)
    {
        _logger              = logger;
        this.providerRuntime = providerRuntime;
        _options             = options.Value;
        _clusterOptions      = clusterOptions.Value;
        _storageName         = name;
        connectionMux        = connection;
    }

#region Implementation of ILifecycleParticipant<ISiloLifecycle>

    public void Participate(ISiloLifecycle observer) => observer.Subscribe(OptionFormattingUtilities.Name<RedisStorage>(_storageName),
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

public static class RedisGrainStorageFactory
{
    internal static IGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<RedisGrainStorageOptions>>();
        var clusterOptions = services.GetProviderClusterOptions(name);

        return ActivatorUtilities.CreateInstance<RedisStorage>(services, Options.Create(optionsMonitor.Get(name)), name, clusterOptions);
    }
}