namespace Argon.Features;

using System.Diagnostics.CodeAnalysis;
using ObservableCollections;
using R3;

public interface IArgonDcRegistry
{
    IReadOnlyDictionary<string, ArgonDcClusterInfo> GetAll();

    bool TryGet(string region, [NotNullWhen(true)] out ArgonDcClusterInfo? item);
    void Upsert(ArgonDcClusterInfo item);
    void Remove(string region);

    ArgonDcClusterInfo? GetNearestDc();

    Task SubscribeToNewClient(Func<ArgonDcClusterInfo, CancellationToken, ValueTask> onNextAsync);
}

public class ArgonDcRegistry : IArgonDcRegistry, IDisposable
{
    private readonly ILogger<IArgonDcRegistry> _logger;
    private readonly System.Threading.Lock     guarder = new();

    private readonly ObservableDictionary<string, ArgonDcClusterInfo> _items = new();

    private readonly Subject<ArgonDcClusterInfo> onAddedNew = new();

    public IReadOnlyDictionary<string, ArgonDcClusterInfo> GetAll() => _items.AsReadOnly();

    public ArgonDcRegistry(ILogger<IArgonDcRegistry> logger)
    {
        _logger = logger;
        _items.ObserveChanged().Subscribe(State);
    }

    private void State(CollectionChangedEvent<KeyValuePair<string, ArgonDcClusterInfo>> obj)
        => _logger.LogWarning("Registry changed, {action}, dc: {key}, {status}", obj.Action, obj.NewItem.Key, obj.NewItem.Value.status);

    public bool TryGet(string dc, [NotNullWhen(true)] out ArgonDcClusterInfo? item)
    {
        using var _ = guarder.EnterScope();
        return _items.TryGetValue(dc, out item);
    }

    public void Upsert(ArgonDcClusterInfo item)
    {
        using var _ = guarder.EnterScope();

        _items[item.dc] = item;
        if (item.status is ArgonDataCenterStatus.ADDED)
            onAddedNew.OnNext(item);
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
           .Where(x => x.status == ArgonDataCenterStatus.ONLINE)
           .OrderByDescending(x => x.effectivity)
           .FirstOrDefault();
    }

    public Task SubscribeToNewClient(Func<ArgonDcClusterInfo, CancellationToken, ValueTask> onNextAsync)
    {
        onAddedNew.SubscribeAwait(onNextAsync, AwaitOperation.Parallel);
        return Task.CompletedTask;
    }

    public void Dispose()
        => onAddedNew.Dispose();
}