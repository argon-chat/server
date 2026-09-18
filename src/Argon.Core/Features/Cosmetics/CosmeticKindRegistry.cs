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
/// <para><b>What a deleted kind leaves behind.</b> Catalogue and equipped rows keep their
/// <c>KindKey</c>, and every path that reads them goes through <see cref="TryGet"/> first, so an
/// orphan is never served, never validated and never equippable. It is also never deleted on its
/// own: a kind pulled for a release and restored in the next one would otherwise take every
/// customer's equipped item with it. <c>ReportOrphans</c> is how an operator finds out they are
/// there, and the admin console's purge is how they go.</para>
/// </remarks>
public sealed class CosmeticKindRegistry
{
    private readonly FrozenDictionary<string, CosmeticKindDefinition> byKey;

    public CosmeticKindRegistry(IReadOnlyCollection<CosmeticKindDefinition> definitions)
    {
        EnsureUniqueKeys(definitions);
        EnsureUniqueFeatureFlags(definitions);
        EnsureNoLayerCollision(definitions);
        EnsureNoLegacyCollision(definitions);

        byKey = definitions.ToFrozenDictionary(definition => definition.Key);

        EnsureAxesResolve();
    }

    /// <summary>
    /// Every axis names a kind that exists and is one whose items are options.
    /// </summary>
    /// <remarks>
    /// At configuration rather than at equip, because an axis pointing at nothing is a picker that is
    /// permanently empty — and an empty picker is indistinguishable from an operator not having
    /// published anything yet, right up until somebody goes looking for why.
    /// </remarks>
    private void EnsureAxesResolve()
    {
        foreach (var definition in byKey.Values)
        {
            foreach (var facet in definition.Facets.Values)
            {
                if (!byKey.TryGetValue(facet.OptionKindKey, out var option))
                {
                    throw new CosmeticKindDeclarationException(definition.KindType,
                        $"declares axis '{facet.Id}' over '{facet.OptionKindKey}', which no kind file declares");
                }

                if (!option.IsCompositional)
                {
                    throw new CosmeticKindDeclarationException(definition.KindType,
                        $"declares axis '{facet.Id}' over '{facet.OptionKindKey}', which is worn rather than chosen; call Compositional() on it");
                }
            }
        }
    }

    public IReadOnlyCollection<CosmeticKindDefinition> All => byKey.Values;

    public bool TryGet(string kindKey, [NotNullWhen(true)] out CosmeticKindDefinition? definition)
        => byKey.TryGetValue(kindKey, out definition);

    public CosmeticKindDefinition? Find(string kindKey)
        => byKey.TryGetValue(kindKey, out var definition) ? definition : null;

    public bool Contains(string kindKey) => byKey.ContainsKey(kindKey);

    public IEnumerable<CosmeticKindDefinition> ForSurface(CosmeticSurface surface)
    {
        foreach (var definition in byKey.Values)
        {
            if (definition.RendersOn(surface))
                yield return definition;
        }
    }

    /// <summary>
    /// The single kind whose equipped item projects onto a pre-cosmetics profile field, or null when
    /// this build ships none. <see cref="LegacyCosmeticField.Badges"/> is many-to-one by design and
    /// is not answerable here — use <see cref="ForLegacyBadges"/>.
    /// </summary>
    public CosmeticKindDefinition? ForLegacyField(LegacyCosmeticField field)
    {
        if (field is LegacyCosmeticField.None or LegacyCosmeticField.Badges)
            return null;

        foreach (var definition in byKey.Values)
        {
            if (definition.LegacyField == field)
                return definition;
        }

        return null;
    }

    public IEnumerable<CosmeticKindDefinition> ForLegacyBadges()
    {
        foreach (var definition in byKey.Values)
        {
            if (definition.LegacyField is LegacyCosmeticField.Badges)
                yield return definition;
        }
    }

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

        return new CosmeticKindRegistry(definitions);
    }

    /// <summary>
    /// Reads one kind's declaration. Throws <see cref="CosmeticKindDeclarationException"/> naming the
    /// type when the declaration is incomplete or ambiguous.
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

    private static void EnsureUniqueKeys(IReadOnlyCollection<CosmeticKindDefinition> definitions)
    {
        var seen = new Dictionary<string, Type>(definitions.Count);

        foreach (var definition in definitions)
        {
            if (seen.TryGetValue(definition.Key, out var owner))
            {
                throw new CosmeticKindDeclarationException(definition.KindType,
                    $"claims key '{definition.Key}', which '{owner.FullName}' already declares");
            }

            seen[definition.Key] = definition.KindType;
        }
    }

    /// <summary>
    /// A derived flag key flattens dots to dashes, so two differently-spelled kinds can land on one
    /// flag — and then turning one off turns the other off with it, from a console that shows one
    /// row. Cheap to check, invisible to debug.
    /// </summary>
    private static void EnsureUniqueFeatureFlags(IReadOnlyCollection<CosmeticKindDefinition> definitions)
    {
        var claimed = new Dictionary<string, CosmeticKindDefinition>(definitions.Count);

        foreach (var definition in definitions)
        {
            if (claimed.TryGetValue(definition.FeatureFlagKey, out var other))
            {
                throw new CosmeticKindDeclarationException(definition.KindType,
                    $"derives feature flag '{definition.FeatureFlagKey}', which '{other.Key}' already gates; "
                    + "rename one key or set an explicit flag");
            }

            claimed[definition.FeatureFlagKey] = definition;
        }
    }

    /// <summary>
    /// Two kinds that can appear on the same surface at the same layer have no stable compositing
    /// order, and the one that wins would depend on dictionary order. That is a copy-pasted kind
    /// file nine times out of ten, so it fails the build rather than the render.
    /// </summary>
    private static void EnsureNoLayerCollision(IReadOnlyCollection<CosmeticKindDefinition> definitions)
    {
        var occupied = new Dictionary<(CosmeticSurface Surface, int Layer), CosmeticKindDefinition>();

        foreach (var definition in definitions)
        {
            foreach (var surface in Enum.GetValues<CosmeticSurface>())
            {
                if (surface is CosmeticSurface.None || !definition.RendersOn(surface))
                    continue;

                if (occupied.TryGetValue((surface, definition.Layer), out var other))
                {
                    throw new CosmeticKindDeclarationException(definition.KindType,
                        $"shares layer {definition.Layer} on {surface} with '{other.Key}'; give one of them a layer of its own");
                }

                occupied[(surface, definition.Layer)] = definition;
            }
        }
    }

    private static void EnsureNoLegacyCollision(IReadOnlyCollection<CosmeticKindDefinition> definitions)
    {
        var claimed = new Dictionary<LegacyCosmeticField, CosmeticKindDefinition>();

        foreach (var definition in definitions)
        {
            if (definition.LegacyField is LegacyCosmeticField.None or LegacyCosmeticField.Badges)
                continue;

            if (claimed.TryGetValue(definition.LegacyField, out var other))
            {
                throw new CosmeticKindDeclarationException(definition.KindType,
                    $"projects onto {definition.LegacyField}, which '{other.Key}' already claims; a legacy field carries one id");
            }

            claimed[definition.LegacyField] = definition;
        }
    }
}
