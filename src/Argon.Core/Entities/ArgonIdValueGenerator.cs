namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;

/// <summary>
/// The identifier an entity gets when the code that created it did not give it one.
/// </summary>
/// <remarks>
/// <para>EF Core already fills an unassigned <see cref="Guid"/> key: its own
/// <c>GuidValueGenerator</c> hands out <see cref="Guid.NewGuid"/>, which is a v4. A v4 carries no
/// timestamp, so <see cref="ArgonId.RegionIndexOf"/> answers "cannot tell" and every caller folds
/// that to <see cref="ArgonId.OriginalRegionIndex"/>. The row is then homed in region zero forever:
/// the value is a primary key that foreign keys already point at, so there is no repair pass, no
/// backfill and no re-key — the mistake is as permanent as the row.</para>
///
/// <para>Replacing the default is the fix rather than auditing the call sites. An audit found eight
/// places that construct an entity without an <c>Id</c> and would find a ninth the week somebody
/// adds an entity, because nothing about <c>new SomeEntity { … }</c> looks wrong.</para>
/// </remarks>
public sealed class ArgonIdValueGenerator : ValueGenerator<Guid>
{
    /// <summary>One instance for the whole model: it holds no state and <see cref="ArgonId.New"/> is thread-safe.</summary>
    public static readonly ArgonIdValueGenerator Instance = new();

    public override Guid Next(EntityEntry entry) => ArgonId.New();

    /// <summary>
    /// The value written here is the value that is stored.
    /// </summary>
    /// <remarks>
    /// Answering <see langword="true" /> would tell EF the key is a placeholder to be replaced by
    /// whatever the INSERT returns — a round trip Argon does not make, against a column default that
    /// does not exist. The insert would then write the placeholder and the change tracker would keep
    /// waiting for a real one.
    /// </remarks>
    public override bool GeneratesTemporaryValues => false;
}

/// <summary>
/// Region-tagged identifiers, applied to the model instead of to the call sites.
/// </summary>
/// <remarks>
/// <para>One pass over the finished model rather than a line in each entity's
/// <c>IEntityTypeConfiguration</c>: an entity added without that line still behaves, which is the
/// whole point — the failure being prevented is one of omission, and a convention you must remember
/// to opt into prevents nothing.</para>
///
/// <para>It is deliberately silent about what it skips. <c>RegionTaggedIdTests</c> walks the same
/// model and asserts that every eligible entity really does come out tagged, so a type this pass
/// declines to touch fails there with a name attached, rather than logging a warning into a startup
/// log nobody reads.</para>
/// </remarks>
public static class ArgonIdGeneration
{
    /// <summary>
    /// Gives every <see cref="ArgonEntity"/> with a <see cref="Guid"/> key a
    /// <see cref="ArgonIdValueGenerator"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>This produces no DDL and needs no migration.</b> The generator is client-side: it is
    /// carried by a core annotation that never reaches the relational model, so the column keeps the
    /// <c>ValueGeneratedOnAdd</c> the key convention already gave it and the migrations differ sees
    /// nothing to do. <c>RegionTaggedIdTests.Tagging_an_id_produces_no_migration</c> pins that.</para>
    ///
    /// <para><b>The generator only runs when the property still holds <see cref="Guid.Empty"/>.</b>
    /// That is EF's contract for client-side generation, and it is what makes the eighty-odd explicit
    /// <c>Id = ArgonId.New()</c> assignments harmless duplication instead of a conflict — an id that
    /// was assigned wins, including a deterministic one and including a
    /// <see cref="ArgonId.NewIn"/> that had to inherit a sibling's region. Do not delete those
    /// assignments on the strength of this pass; several of them are minting in a region this process
    /// is not in.</para>
    ///
    /// <para>Called last in <c>OnModelCreating</c>, because it reads the model rather than
    /// contributing to it: an entity type registered after this ran would not be seen.</para>
    /// </remarks>
    public static ModelBuilder UseRegionTaggedIds(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // ArgonEntity<T> and ArgonEntityNoKey are separate roots rather than descendants of
            // ArgonEntity, which is why SpaceInvite's ulong key and FeatureFlagEntity's string key
            // never reach here — and neither does ArgonMessageEntity, whose key is
            // (SpaceId, ChannelId, MessageId) and whose MessageId is a snowflake the message layer
            // assigns, declared ValueGeneratedNever over a unique_rowid() column default.
            if (!typeof(ArgonEntity).IsAssignableFrom(entityType.ClrType))
                continue;

            var key = entityType.FindPrimaryKey();

            if (key is not { Properties.Count: 1 })
                continue;

            var id = key.Properties[0];

            // Both guards are unreachable today and both stay. The single-property one keeps this
            // away from composite keys, where filling in an arbitrary column would be a silent
            // disaster; the Guid one is what keeps it correct the day ArgonEntity grows a descendant
            // keyed by something that is not a Guid.
            if (id.ClrType != typeof(Guid) || !IsUnclaimed(id))
                continue;

            id.SetValueGeneratorFactory(static (_, _) => ArgonIdValueGenerator.Instance);
        }

        return modelBuilder;
    }

    /// <summary>
    /// Whether nothing else has already claimed the right to produce this key's value.
    /// </summary>
    /// <remarks>
    /// <para>An entity that configures <c>ValueGeneratedNever</c> is saying the application assigns
    /// the id and EF must not; one that configures a default value or <c>defaultValueSql</c> is
    /// saying the database does. Overriding either would move where the id comes from without anyone
    /// asking, and in the <c>defaultValueSql</c> case would leave the column default in place as a
    /// second, silently-different answer.</para>
    ///
    /// <para><c>OnAdd</c> exactly, rather than "generates on add at all", because that is the one
    /// state the key convention produces on its own — a single non-foreign-key Guid primary key.
    /// Anything else was written deliberately by the entity and is not this pass's to reinterpret.</para>
    ///
    /// <para>Nothing in the model trips any of this today. It exists so that the day something does,
    /// it is excluded here and named by the test rather than quietly overridden.</para>
    /// </remarks>
    private static bool IsUnclaimed(IMutableProperty id)
        => id.ValueGenerated is ValueGenerated.OnAdd
        && id.GetValueGeneratorFactory() is null
        && id.GetDefaultValueSql() is null
        && !id.TryGetDefaultValue(out _);
}
