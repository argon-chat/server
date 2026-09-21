namespace Argon.Features.Cosmetics;

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Every cosmetic kind the build contains, read once from the assembly by invoking each kind's
/// static <see cref="ICosmeticKind.Describe"/> — no instances are constructed.
/// </summary>
/// <remarks>
/// <para>The registry is the whole of "does this kind exist". A file added under <c>Kinds/</c> is in
/// it on the next build; a file deleted is not, and nothing else has to be touched for either.</para>
///
/// <para><b>What a deleted kind leaves behind.</b> Catalogue rows keep their <c>KindKey</c>, and
/// every path that reads them goes through <see cref="TryGet"/> first, so an orphan is never served
/// and never validated. It is also never deleted on its own: a kind pulled for a release and
/// restored in the next one would otherwise take every customer's item with it.</para>
///
/// <para><b>Built by <see cref="Create"/> rather than by the constructor.</b> Reading a declaration
/// can fail — two kinds on one layer, an axis over a kind nobody declared — and a constructor that
/// throws leaves a half-built object behind and a stack trace that says <c>.ctor</c>. The checks are
/// a named step with a name in the message, and the constructor does nothing but assign.</para>
/// </remarks>
public sealed class CosmeticKindRegistry
{
    /// <summary>Allocated once: the collision check reads it per kind and it never changes.</summary>
    private static readonly CosmeticSurface[] Surfaces = Enum.GetValues<CosmeticSurface>();

    private readonly FrozenDictionary<string, CosmeticKindDefinition> byKey;

    private CosmeticKindRegistry(FrozenDictionary<string, CosmeticKindDefinition> byKey)
        => this.byKey = byKey;

    public IReadOnlyCollection<CosmeticKindDefinition> All => byKey.Values;

    public bool TryGet(string kindKey, [NotNullWhen(true)] out CosmeticKindDefinition? definition)
        => byKey.TryGetValue(kindKey, out definition);

    public CosmeticKindDefinition? Find(string kindKey)
        => byKey.TryGetValue(kindKey, out var definition) ? definition : null;

    public bool Contains(string kindKey) => byKey.ContainsKey(kindKey);

    public static CosmeticKindRegistry FromAssembly(Assembly assembly)
        => FromTypes(assembly.GetTypes());

    public static CosmeticKindRegistry FromTypes(IEnumerable<Type> types)
    {
        var definitions = new List<CosmeticKindDefinition>();

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || !typeof(ICosmeticKind).IsAssignableFrom(type))
                continue;

            definitions.Add(Describe(type));
        }

        return Create(definitions);
    }

    /// <summary>
    /// Checks a set of declarations against each other and, if they agree, builds the registry.
    /// </summary>
    /// <remarks>
    /// One pass over the kinds answers every question that is about a kind on its own or about a
    /// pair of them — the key, the derived flag, the layer on each surface. Axes need the whole map
    /// before they can be resolved, so they are the second and last pass.
    /// </remarks>
    public static CosmeticKindRegistry Create(IReadOnlyCollection<CosmeticKindDefinition> definitions)
    {
        var byKey    = new Dictionary<string, CosmeticKindDefinition>(definitions.Count);
        var byFlag   = new Dictionary<string, CosmeticKindDefinition>(definitions.Count);
        var occupied = new Dictionary<(CosmeticSurface Surface, int Layer), CosmeticKindDefinition>();

        foreach (var definition in definitions)
        {
            if (!byKey.TryAdd(definition.Key, definition))
            {
                throw new CosmeticKindDeclarationException(definition,
                    $"claims key '{definition.Key}', which '{byKey[definition.Key].DeclaredBy}' already declares");
            }

            // A derived flag key flattens dots to dashes, so two differently-spelled kinds can land
            // on one flag — and then turning one off turns the other off with it, from a console
            // that shows one row. Cheap to check, invisible to debug.
            if (!byFlag.TryAdd(definition.FeatureFlagKey, definition))
            {
                throw new CosmeticKindDeclarationException(definition,
                    $"derives feature flag '{definition.FeatureFlagKey}', which '{byFlag[definition.FeatureFlagKey].Key}' already gates; "
                    + "rename one key or set an explicit flag");
            }

            // Two kinds that can appear on the same surface at the same layer have no stable
            // compositing order, and the one that wins would depend on dictionary order. That is a
            // copy-pasted kind file nine times out of ten, so it fails the build rather than the
            // render.
            foreach (var surface in Surfaces)
            {
                if (surface is CosmeticSurface.None || !definition.RendersOn(surface))
                    continue;

                if (!occupied.TryAdd((surface, definition.Layer), definition))
                {
                    throw new CosmeticKindDeclarationException(definition,
                        $"shares layer {definition.Layer} on {surface} with '{occupied[(surface, definition.Layer)].Key}'; "
                        + "give one of them a layer of its own");
                }
            }
        }

        EnsureAxesResolve(byKey);

        return new CosmeticKindRegistry(byKey.ToFrozenDictionary());
    }

    /// <summary>
    /// Reads one kind's declaration. Throws <see cref="CosmeticKindDeclarationException"/> naming
    /// the type when the declaration is incomplete or ambiguous.
    /// </summary>
    public static CosmeticKindDefinition Describe(Type kindType)
    {
        if (!typeof(ICosmeticKind).IsAssignableFrom(kindType))
            throw new CosmeticKindDeclarationException(kindType, $"does not implement {nameof(ICosmeticKind)}");
        if (kindType.IsAbstract || kindType.IsInterface)
            throw new CosmeticKindDeclarationException(kindType, "must be a concrete class");

        var describe = kindType.GetMethod(nameof(ICosmeticKind.Describe),
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
            [typeof(ICosmeticKindDescriptor)]);

        if (describe is null)
        {
            throw new CosmeticKindDeclarationException(kindType,
                $"has no public static {nameof(ICosmeticKind.Describe)}({nameof(ICosmeticKindDescriptor)})");
        }

        var descriptor = new CosmeticKindDescriptor(kindType);
        describe.Invoke(null, [descriptor]);

        return descriptor.Build();
    }

    /// <summary>
    /// Every axis names a kind that exists and is one whose items are options.
    /// </summary>
    /// <remarks>
    /// At configuration rather than at equip, because an axis pointing at nothing is a picker that
    /// is permanently empty — and an empty picker is indistinguishable from an operator not having
    /// published anything yet, right up until somebody goes looking for why.
    /// </remarks>
    private static void EnsureAxesResolve(Dictionary<string, CosmeticKindDefinition> byKey)
    {
        foreach (var definition in byKey.Values)
        {
            foreach (var facet in definition.Facets.Values)
            {
                if (!byKey.TryGetValue(facet.OptionKindKey, out var option))
                {
                    throw new CosmeticKindDeclarationException(definition,
                        $"declares axis '{facet.Id}' over '{facet.OptionKindKey}', which no kind file declares");
                }

                if (!option.IsCompositional)
                {
                    throw new CosmeticKindDeclarationException(definition,
                        $"declares axis '{facet.Id}' over '{facet.OptionKindKey}', which is worn rather than chosen; call Compositional() on it");
                }
            }
        }
    }
}
