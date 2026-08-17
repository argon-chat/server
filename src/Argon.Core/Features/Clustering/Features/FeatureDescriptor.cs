namespace Argon.Features.Clustering;

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

    public override string ToString()
        => Name;
}

internal sealed class FeatureDescriptor(Type featureType) : IFeatureDescriptor
{
    private readonly List<Type>                             requiredFeatures    = [];
    private readonly List<Type>                             afterFeatures       = [];
    private readonly List<Type>                             beforeFeatures      = [];
    private readonly List<Type>                             conflictingFeatures = [];
    private readonly List<Action<IGrainCollectionRegistry>> grainRootHooks      = [];

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

    public FeatureDefinition Build()
        => new()
        {
            FeatureType = featureType,
            Name        = featureName,
            Description = featureDescription,
            Requires    = requiredFeatures.Distinct().ToArray(),
            After       = afterFeatures.Distinct().ToArray(),
            Before      = beforeFeatures.Distinct().ToArray(),
            Conflicts   = conflictingFeatures.Distinct().ToArray(),
            GrainRoots  = grainRootHooks.ToArray()
        };
}
