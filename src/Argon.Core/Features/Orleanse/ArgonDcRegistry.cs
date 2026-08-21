namespace Argon.Features;

using System.Diagnostics.CodeAnalysis;
using Env;
using ObservableCollections;
using R3;
using static Api.Features.Orleans.Client.ArgonDataCenterStatus;

public interface IArgonDcRegistry
{
    IReadOnlyDictionary<string, ArgonDcClusterInfo> GetAll();

    bool TryGet(string region, [NotNullWhen(true)] out ArgonDcClusterInfo? item);
    void Upsert(ArgonDcClusterInfo item);
    void Remove(string region);

    ArgonDcClusterInfo? GetNearestDc();
    IClusterClient?     GetNearestClusterClient();

    int GetDcCount();

    Task SubscribeToNewClient(Func<ArgonDcClusterInfo, CancellationToken, ValueTask> onNextAsync);
}

public class ArgonHybridDcRegistry(IServiceProvider serviceProvider) : IArgonDcRegistry
{
    public IReadOnlyDictionary<string, ArgonDcClusterInfo> GetAll()
        => new Dictionary<string, ArgonDcClusterInfo>()
        {
            {
                "ru-3", new ArgonDcClusterInfo("ru-3", 1, serviceProvider, DateTime.UtcNow, ONLINE, new CancellationTokenSource())
            }
        };

    public bool TryGet(string region, [NotNullWhen(true)] out ArgonDcClusterInfo? item)
    {
        item = GetAll().First().Value;
        return true;
    }

    public void Upsert(ArgonDcClusterInfo item)
    {

    }

    public void Remove(string region)
    {

    }

    public ArgonDcClusterInfo? GetNearestDc()
        => GetAll().First().Value;

    public IClusterClient? GetNearestClusterClient()
        => serviceProvider.GetRequiredService<IClusterClient>();

    public int GetDcCount()
        => 1;

    public Task SubscribeToNewClient(Func<ArgonDcClusterInfo, CancellationToken, ValueTask> onNextAsync)
        => Task.CompletedTask;
}

public class ArgonDcRegistry : IArgonDcRegistry, IDisposable
{
    private readonly ILogger<IArgonDcRegistry> _logger;
    private readonly IHostEnvironment          _env;
    private readonly IServiceProvider          _serviceProvider;
    private readonly string                    _currentDc;
    private readonly Lock                      guarder = new();

    private readonly ObservableDictionary<string, ArgonDcClusterInfo> _items = new();

    private readonly Subject<ArgonDcClusterInfo> onAddedNew = new();

    public IReadOnlyDictionary<string, ArgonDcClusterInfo> GetAll() => _items.AsReadOnly();

    public ArgonDcRegistry(ILogger<IArgonDcRegistry> logger, IHostEnvironment env, IServiceProvider serviceProvider, [FromKeyedServices("dc")] string currentDc)
    {
        _logger               = logger;
        _env                  = env;
        _serviceProvider = serviceProvider;
        _currentDc            = currentDc;
        _items.ObserveChanged().Subscribe(State);
    }

    private void State(CollectionChangedEvent<KeyValuePair<string, ArgonDcClusterInfo>> obj)
        => _logger.LogWarning("Registry changed, {action}, dc: {key}, {status}, effectivity: {effectivity}", obj.Action, obj.NewItem.Key,
            obj.NewItem.Value.status, obj.NewItem.Value.effectivity);

    public bool TryGet(string dc, [NotNullWhen(true)] out ArgonDcClusterInfo? item)
    {
        using var _ = guarder.EnterScope();
        return _items.TryGetValue(dc, out item);
    }

    public void Upsert(ArgonDcClusterInfo item)
    {
        using var _ = guarder.EnterScope();

        if (item.status is CREATED)
        {
            _items[item.dc] = item with
            {
                status = WAIT_CONNECT,
                effectivity = item.dc.Equals(_currentDc) ? float.PositiveInfinity : item.effectivity
            };
            onAddedNew.OnNext(item);
            return;
        }

        if (_items.TryGetValue(item.dc, out var result) && result != item)
            _items[item.dc] = item;
    }

    public void Remove(string dc)
    {
        using var _ = guarder.EnterScope();
        _items.Remove(dc);
    }

    public ArgonDcClusterInfo? GetNearestDc()
    {
        using var _ = guarder.EnterScope();

        return _items
           .Select(x => x.Value)
           .Where(x => x.status == ONLINE)
           .OrderByDescending(x => x.effectivity)
           .FirstOrDefault();
    }

    public IClusterClient? GetNearestClusterClient()
    {
        if (!_env.IsMultiRegion())
            return _serviceProvider.GetRequiredService<IClusterClient>();

        var nearest = GetNearestDc();

        return nearest?.serviceProvider.GetRequiredService<IClusterClient>();
    }

    public int GetDcCount()
        => _items.Count;

    public Task SubscribeToNewClient(Func<ArgonDcClusterInfo, CancellationToken, ValueTask> onNextAsync)
    {
        onAddedNew.SubscribeAwait(onNextAsync, AwaitOperation.Parallel);
        return Task.CompletedTask;
    }

    public void Dispose()
        => onAddedNew.Dispose();
}