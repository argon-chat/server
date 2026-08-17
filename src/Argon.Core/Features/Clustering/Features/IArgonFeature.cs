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
    /// Declares a configuration section this feature owns and the type it binds to.
    /// </summary>
    /// <param name="section">
    /// Configuration path, colon-separated. Defaults to the feature's name, which is what a new
    /// section should be called; pass it explicitly only to keep an existing key that deployments
    /// already set.
    /// </param>
    /// <remarks>
    /// What counts as a valid value is <typeparamref name="TOptions"/>'s business, not this call's:
    /// the <c>required</c> keyword, the data annotations, and
    /// <see cref="IValidatableFeatureOptions"/> for anything conditional. Keeping the rules on the
    /// model is what stops a setting's meaning from being split between two files.
    /// <para>
    /// Ownership is enforced: a per-feature file under <c>conf.d/</c> may only set sections declared
    /// here, which is what keeps one feature's file from quietly reconfiguring another's.
    /// Call this more than once for a feature that binds more than one options class.
    /// </para>
    /// </remarks>
    IFeatureDescriptor Options<TOptions>(string? section = null) where TOptions : class;

    /// <summary>
    /// Grain analysis roots this feature brings with it — the services it registers that call
    /// grains. Applied to the enabling role's registry.
    /// </summary>
    IFeatureDescriptor GrainRoots(Action<IGrainCollectionRegistry> configure);
}
