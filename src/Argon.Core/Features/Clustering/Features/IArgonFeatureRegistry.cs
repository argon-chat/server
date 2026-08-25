namespace Argon.Features.Clustering;

/// <summary>
/// Collects the features a role enables. Transitive <see cref="IFeatureDescriptor.Requires{T}"/>
/// dependencies are resolved later by <see cref="FeatureGraph"/>, not here.
/// </summary>
public interface IArgonFeatureRegistry
{
    void Add<TFeature>() where TFeature : IArgonFeature, new();
}

internal sealed class ArgonFeatureRegistry : IArgonFeatureRegistry
{
    private readonly List<Type> declared = [];

    public IReadOnlyList<Type> Declared
        => declared;

    public void Add<TFeature>() where TFeature : IArgonFeature, new()
    {
        if (!declared.Contains(typeof(TFeature)))
            declared.Add(typeof(TFeature));
    }
}
