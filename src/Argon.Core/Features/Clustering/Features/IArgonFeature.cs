namespace Argon.Features.Clustering;

/// <summary>
/// A named unit of DI and pipeline configuration with declared ordering relative to other features.
/// </summary>
/// <remarks>
/// Implementations must be non-abstract classes with a public parameterless constructor.
/// <see cref="Describe"/> is static so the feature graph can be built — and validated — without
/// constructing anything.
/// </remarks>
public interface IArgonFeature
{
    /// <summary>
    /// Optional. A feature that only needs a name and no edges can omit this entirely — the name is
    /// derived from the type (<c>BotPathTokenFeature</c> becomes <c>bot-path-token</c>).
    /// </summary>
    static virtual void Describe(IFeatureDescriptor descriptor)
    {
    }

    /// <summary>Registers services. Called in topological order.</summary>
    void Configure(ArgonFeatureContext ctx)
    {
    }

    /// <summary>Maps endpoints and middleware. Called in the same topological order.</summary>
    void Map(ArgonEndpointContext ctx)
    {
    }
}

/// <summary>
/// Fluent declaration of a feature's identity, its edges in the feature graph, and the grain
/// analysis roots it contributes to whichever role enables it.
/// </summary>
public interface IFeatureDescriptor
{
    /// <summary>Stable name used in diagnostics and <c>--explain</c> output.</summary>
    IFeatureDescriptor Named(string name);

    IFeatureDescriptor Describing(string description);

    /// <summary>
    /// Hard dependency: <typeparamref name="TFeature"/> is pulled into any role that enables this
    /// one, and is configured before it.
    /// </summary>
    IFeatureDescriptor Requires<TFeature>() where TFeature : IArgonFeature;

    /// <summary>
    /// Ordering only: if <typeparamref name="TFeature"/> is also enabled it is configured first.
    /// Does not pull it in.
    /// </summary>
    IFeatureDescriptor After<TFeature>() where TFeature : IArgonFeature;

    /// <summary>
    /// Ordering only: if <typeparamref name="TFeature"/> is also enabled it is configured after
    /// this one. Does not pull it in.
    /// </summary>
    IFeatureDescriptor Before<TFeature>() where TFeature : IArgonFeature;

    /// <summary>
    /// Declares that the two features cannot be enabled by the same role. Reported as E8.
    /// </summary>
    IFeatureDescriptor Conflicts<TFeature>() where TFeature : IArgonFeature;

    /// <summary>
    /// Grain analysis roots this feature brings with it — the services it registers that call
    /// grains. Applied to the enabling role's registry.
    /// </summary>
    IFeatureDescriptor GrainRoots(Action<IGrainCollectionRegistry> configure);
}
