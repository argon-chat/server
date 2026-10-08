namespace Argon.Features.Clustering;

using Features.Orleanse.Storages;

/// <summary>
/// What the product adds to the Orleans host. The host itself knows no grain, contract or converter;
/// everything of that kind arrives through here, filled in by the assembly that owns the grains.
/// </summary>
public sealed class ArgonOrleansProfile
{
    /// <summary>
    /// Storage providers registered on every silo, identical rather than declared per role: a role
    /// never has to register a provider on another role's behalf. "Default" is always among them.
    /// </summary>
    public List<string> StorageProviders { get; } = ["Default"];

    /// <summary>
    /// Applied to the catch-all Newtonsoft serializer ahead of the runtime's own converters, so a
    /// converter for a product type wins over the generic Ion and enum ones.
    /// </summary>
    public List<Action<JsonSerializerSettings>> Serializer { get; } = [];

    /// <summary>Run against the host builder on every role, silo and client alike.</summary>
    public List<Action<WebApplicationBuilder>> Host { get; } = [];

    /// <summary>
    /// Services a cluster client built for another datacenter resolves from this host's container
    /// rather than constructing for itself.
    /// </summary>
    public List<Type> ForeignClientServices { get; } = [];

    /// <summary>
    /// The call a silo makes at start for each grain contract a role lists in
    /// <see cref="RoleDescriptor.StartupCalls"/>. A declaration without a task here fails the boot.
    /// </summary>
    public Dictionary<Type, Func<IServiceProvider, CancellationToken, Task>> StartupTasks { get; } = new();

    public IReadOnlySet<string> KnownStorageProviders
        => StorageProviders.Append(VolatileGrainStorage.ProviderName).ToHashSet(StringComparer.Ordinal);

    public ArgonOrleansProfile OnStartup<TGrain>(Guid key, Func<TGrain, ValueTask> call) where TGrain : IGrainWithGuidKey
    {
        StartupTasks[typeof(TGrain)] = async (sp, _) => await call(sp.GetRequiredService<IGrainFactory>().GetGrain<TGrain>(key));
        return this;
    }
}
