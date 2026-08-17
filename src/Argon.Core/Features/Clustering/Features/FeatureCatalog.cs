namespace Argon.Features.Clustering;

/// <summary>
/// Reads and caches <see cref="FeatureDefinition"/>s by invoking each feature type's static
/// <see cref="IArgonFeature.Describe"/> through reflection — no instances are constructed.
/// </summary>
public static class FeatureCatalog
{
    private static readonly ConcurrentDictionary<Type, FeatureDefinition> cache = new();

    public static FeatureDefinition Describe(Type featureType)
        => cache.GetOrAdd(featureType, static t =>
        {
            if (!typeof(IArgonFeature).IsAssignableFrom(t))
                throw new ArgumentException($"'{t.FullName}' does not implement {nameof(IArgonFeature)}", nameof(featureType));
            if (t.IsAbstract || t.IsInterface)
                throw new ArgumentException($"Feature '{t.FullName}' must be a concrete class", nameof(featureType));

            // Describe is optional: a feature with no edges and a name that follows from its type
            // declares nothing at all.
            var describe = t.GetMethod(nameof(IArgonFeature.Describe),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                [typeof(IFeatureDescriptor)]);

            var descriptor = new FeatureDescriptor(t);
            describe?.Invoke(null, [descriptor]);
            return descriptor.Build();
        });

    public static FeatureDefinition Describe<TFeature>() where TFeature : IArgonFeature
        => Describe(typeof(TFeature));

    internal static void ResetForTesting()
        => cache.Clear();
}
