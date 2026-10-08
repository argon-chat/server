namespace Argon.Features.Clustering;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Immutable result of running <see cref="IArgonFeature.Describe"/> against a feature type.
/// </summary>
public sealed class FeatureDefinition
{
    public required Type   FeatureType { get; init; }
    public required string Name        { get; init; }
    public          string Description { get; init; } = string.Empty;

    /// <summary>Hard dependencies — pulled in transitively and ordered before this feature.</summary>
    public required IReadOnlyList<Type> Requires { get; init; }

    /// <summary>Ordering-only edges honoured when the other feature happens to be enabled.</summary>
    public required IReadOnlyList<Type> After { get; init; }

    public required IReadOnlyList<Type> Before { get; init; }

    public required IReadOnlyList<Type> Conflicts { get; init; }

    /// <summary>Grain analysis roots contributed to the enabling role.</summary>
    public required IReadOnlyList<Action<IGrainCollectionRegistry>> GrainRoots { get; init; }

    /// <summary>
    /// Configuration sections this feature owns. Empty for a feature with nothing to configure —
    /// which is a real answer, not an omission.
    /// </summary>
    public required IReadOnlyList<FeatureOptionsBinding> Options { get; init; }

    /// <summary>
    /// MVC controllers this feature owns. Only these are routed on a role that enables it.
    /// </summary>
    public required IReadOnlyList<Type> Controllers { get; init; }

    public override string ToString()
        => Name;
}

internal sealed class FeatureDescriptor(Type featureType) : IFeatureDescriptor
{
    private readonly List<Type>                             requiredFeatures    = [];
    private readonly List<Type>                             afterFeatures       = [];
    private readonly List<Type>                             beforeFeatures      = [];
    private readonly List<Type>                             conflictingFeatures = [];
    private readonly List<Action<IGrainCollectionRegistry>>       grainRootHooks  = [];
    private readonly List<Func<string, FeatureOptionsBinding>>    optionsBindings = [];
    private readonly List<Type>                                  ownedControllers = [];

    private string featureName        = DeriveName(featureType);
    private string featureDescription = string.Empty;

    /// <summary>
    /// <c>BotPathTokenFeature</c> becomes <c>bot-path-token</c>. Naming a feature by hand is then
    /// only for the cases where the type name is not the name you want.
    /// </summary>
    internal static string DeriveName(Type featureType)
    {
        var name = featureType.Name;
        if (name.EndsWith("Feature", StringComparison.Ordinal) && name.Length > "Feature".Length)
            name = name[..^"Feature".Length];

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && !char.IsUpper(name[i - 1]))
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    public IFeatureDescriptor Named(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"Feature '{featureType.FullName}' declared an empty name", nameof(name));
        featureName = name;
        return this;
    }

    public IFeatureDescriptor Describing(string description)
    {
        featureDescription = description;
        return this;
    }

    public IFeatureDescriptor Requires<TFeature>() where TFeature : IArgonFeature
    {
        requiredFeatures.Add(typeof(TFeature));
        return this;
    }

    public IFeatureDescriptor After<TFeature>() where TFeature : IArgonFeature
    {
        afterFeatures.Add(typeof(TFeature));
        return this;
    }

    public IFeatureDescriptor Before<TFeature>() where TFeature : IArgonFeature
    {
        beforeFeatures.Add(typeof(TFeature));
        return this;
    }

    public IFeatureDescriptor Conflicts<TFeature>() where TFeature : IArgonFeature
    {
        conflictingFeatures.Add(typeof(TFeature));
        return this;
    }

    public IFeatureDescriptor GrainRoots(Action<IGrainCollectionRegistry> configure)
    {
        grainRootHooks.Add(configure);
        return this;
    }

    public IFeatureDescriptor Controller<TController>() where TController : ControllerBase
    {
        ownedControllers.Add(typeof(TController));
        return this;
    }

    /// <summary>
    /// Deferred because the section defaults to the feature's name, and <see cref="Named"/> is free
    /// to come after this call in the fluent chain.
    /// </summary>
    public IFeatureDescriptor Options<TOptions>(string? section = null)
        where TOptions : class
    {
        optionsBindings.Add(name => FeatureOptionsBinding.Create<TOptions>(
            name, string.IsNullOrWhiteSpace(section) ? name : section));
        return this;
    }

    public FeatureDefinition Build()
    {
        var options = optionsBindings.Select(create => create(featureName)).ToArray();

        if (options.Select(o => o.Section).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Length)
            throw new InvalidOperationException(
                $"Feature '{featureType.FullName}' declares the same configuration section twice: " +
                string.Join(", ", options.Select(o => o.Section)));

        return new FeatureDefinition
        {
            FeatureType = featureType,
            Name        = featureName,
            Description = featureDescription,
            Requires    = requiredFeatures.Distinct().ToArray(),
            After       = afterFeatures.Distinct().ToArray(),
            Before      = beforeFeatures.Distinct().ToArray(),
            Conflicts   = conflictingFeatures.Distinct().ToArray(),
            GrainRoots  = grainRootHooks.ToArray(),
            Options     = options,
            Controllers = ownedControllers.Distinct().ToArray()
        };
    }
}
