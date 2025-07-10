namespace Argon.Cassandra.Mapping;

using Core;

internal class EntityMetadataContext(CassandraDbContext ctx) : IEntityMetadataContext
{
    private readonly List<IEntityMetadataBuilder> builders = [];

    public void Build()
    {
        foreach (var metadata in builders.Select(builder => builder.Build()))
            EntityMetadataCache.OnGenerateEntityMetadata(metadata.EntityType, metadata);
    }

    public IEntityMetadataBuilder<T> ForTable<T>(string? tableName = null, string? keyspace = null) where T : class
    {
        tableName ??= typeof(T).Name;
        var builder = new EntityMetadataBuilder<T>().WithTable(tableName, keyspace);

        builders.Add(builder);

        return builder;
    }
}