namespace Argon.Features.EF;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Internal;

#pragma warning disable EF1001

/// <summary>
/// Hash-sharded primary keys and indexes: <c>USING HASH</c> on CockroachDB, nothing on PostgreSQL.
/// </summary>
/// <remarks>
/// For keys that grow monotonically (UUIDv7 ids, timestamps), which otherwise send every insert to the
/// last range of the table. <see cref="ArgonRelationalAnnotationProvider"/> carries both annotations into
/// the relational model so migrations pick changes up; <c>MultiregionalMigrationsSqlGenerator</c> turns
/// them into SQL. On an existing table the key annotation becomes an <c>AlterTableOperation</c>, which
/// Npgsql's generator ignores and the Cockroach one turns into <c>ALTER PRIMARY KEY</c>.
/// </remarks>
public static class CockroachHashSharding
{
    /// <summary>On an entity type: its table's primary key is hash-sharded.</summary>
    public const string KeyAnnotation = "Cockroach:HashShardedKey";

    /// <summary>On an index.</summary>
    public const string IndexAnnotation = "Cockroach:HashSharded";

    public static EntityTypeBuilder HasHashShardedKey(this EntityTypeBuilder builder)
    {
        builder.HasAnnotation(KeyAnnotation, true);
        return builder;
    }

    public static IndexBuilder<T> IsHashSharded<T>(this IndexBuilder<T> builder)
    {
        builder.HasAnnotation(IndexAnnotation, true);
        return builder;
    }

    public static bool IsHashShardedKey(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(KeyAnnotation)?.Value is true;

    /// <summary>
    /// Registers <see cref="ArgonRelationalAnnotationProvider"/>. On both engines: the migrations differ
    /// has to see the same model whichever engine the tooling happens to be configured for.
    /// </summary>
    public static DbContextOptionsBuilder UseArgonSchemaAnnotations(this DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<IRelationalAnnotationProvider, ArgonRelationalAnnotationProvider>();
}

/// <summary>
/// Npgsql's annotation provider plus the two <see cref="CockroachHashSharding"/> annotations, which EF
/// would otherwise leave on the entity model where the migrations differ never looks.
/// </summary>
public class ArgonRelationalAnnotationProvider(RelationalAnnotationProviderDependencies dependencies)
    : NpgsqlAnnotationProvider(dependencies)
{
    public override IEnumerable<IAnnotation> For(ITable table, bool designTime)
    {
        foreach (var annotation in base.For(table, designTime))
            yield return annotation;

        if (designTime && table.EntityTypeMappings.Any(m => m.TypeBase.FindAnnotation(CockroachHashSharding.KeyAnnotation)?.Value is true))
            yield return new Annotation(CockroachHashSharding.KeyAnnotation, true);
    }

    public override IEnumerable<IAnnotation> For(ITableIndex index, bool designTime)
    {
        foreach (var annotation in base.For(index, designTime))
            yield return annotation;

        if (designTime && index.MappedIndexes.Any(i => i.FindAnnotation(CockroachHashSharding.IndexAnnotation)?.Value is true))
            yield return new Annotation(CockroachHashSharding.IndexAnnotation, true);
    }
}
