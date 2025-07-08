namespace Argon.Cassandra.Mapping;

using Core;

public interface IEntityMetadataContext
{
    IEntityMetadataBuilder<T> ForTable<T>(string? tableName = null, string? keyspace = null) where T : class;
}

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

public interface IEntityMetadataBuilder
{
    EntityMetadata Build();
}

public interface IEntityMetadataBuilder<TEntity> : IEntityMetadataBuilder
{
    IPropertyBuilder<TEntity> WithProperty<TProperty>(Expression<Func<TEntity, TProperty>> selector);

    IPropertyBuilder<TEntity> WithPartitionKey<TProp>(Expression<Func<TEntity, TProp>> selector, int order)
        => WithProperty(selector).AsPartitionKey(order);

    IPropertyBuilder<TEntity> WithClusteringKey<TProp>(Expression<Func<TEntity, TProp>> selector, int order)
        => WithProperty(selector).AsClusteringKey(order);

    IPropertyBuilder<TEntity> WithStatic<TProp>(Expression<Func<TEntity, TProp>> selector)
        => WithProperty(selector).AsStatic();

    IPropertyBuilder<TEntity> WithCounter<TProp>(Expression<Func<TEntity, TProp>> selector)
        => WithProperty(selector).AsCounter();

    IPropertyBuilder<TEntity> WithIndex<TProp>(Expression<Func<TEntity, TProp>> selector)
        => WithProperty(selector).AsIndexed();
}

public interface IPropertyBuilder<TEntity>
{
    IPropertyBuilder<TEntity> WithColumn(string name);
    IPropertyBuilder<TEntity> AsPartitionKey(int order = 0);
    IPropertyBuilder<TEntity> AsClusteringKey(int order = 0);
    IPropertyBuilder<TEntity> AsStatic();
    IPropertyBuilder<TEntity> AsCounter();
    IPropertyBuilder<TEntity> AsIndexed();

    IPropertyBuilder<TEntity> WithProperty<TProp>(Expression<Func<TEntity, TProp>> selector);

    IPropertyBuilder<TEntity> WithPartitionKey<TProp>(Expression<Func<TEntity, TProp>> selector, int order)
        => WithProperty(selector).AsPartitionKey(order);

    IPropertyBuilder<TEntity> WithClusteringKey<TProp>(Expression<Func<TEntity, TProp>> selector, int order)
        => WithProperty(selector).AsClusteringKey(order);

    IPropertyBuilder<TEntity> WithStatic<TProp>(Expression<Func<TEntity, TProp>> selector)
        => WithProperty(selector).AsStatic();

    IPropertyBuilder<TEntity> WithCounter<TProp>(Expression<Func<TEntity, TProp>> selector)
        => WithProperty(selector).AsCounter();

    IPropertyBuilder<TEntity> WithIndex<TProp>(Expression<Func<TEntity, TProp>> selector)
        => WithProperty(selector).AsIndexed();

    IPropertyBuilder<TEntity> WithConverter<T>() where T : ICassandraConverter;
}

public class EntityMetadataBuilder<TEntity> : IEntityMetadataBuilder<TEntity>
{
    private readonly Type    entityType = typeof(TEntity);
    private          string? tableName;
    private          string? keyspace;

    private readonly List<PropertyInfo>                            properties        = new();
    private readonly Dictionary<PropertyInfo, string>              columnMappings    = new();
    private readonly Dictionary<int, PropertyInfo>                 partitionKeys     = new();
    private readonly Dictionary<int, PropertyInfo>                 clusteringKeys    = new();
    private readonly List<PropertyInfo>                            staticColumns     = new();
    private readonly List<PropertyInfo>                            counterColumns    = new();
    private readonly List<PropertyInfo>                            indexedProperties = new();
    private          Dictionary<PropertyInfo, ICassandraConverter> converters        = new();

    public EntityMetadataBuilder<TEntity> WithTable(string tableName, string? keyspace = null)
    {
        this.tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        this.keyspace  = keyspace;
        return this;
    }

    public IPropertyBuilder<TEntity> WithProperty<TProperty>(Expression<Func<TEntity, TProperty>> selector)
    {
        var property = GetPropertyInfo(selector);

        if (!properties.Contains(property))
            properties.Add(property);

        return new PropertyBuilder(this, property);
    }

    private class PropertyBuilder(EntityMetadataBuilder<TEntity> builder, PropertyInfo property) : IPropertyBuilder<TEntity>
    {
        public IPropertyBuilder<TEntity> WithColumn(string name)
        {
            builder.columnMappings[property] = name;
            return this;
        }

        public IPropertyBuilder<TEntity> AsPartitionKey(int order = 0)
        {
            builder.partitionKeys[order] = property;
            return this;
        }

        public IPropertyBuilder<TEntity> AsClusteringKey(int order = 0)
        {
            builder.clusteringKeys[order] = property;
            return this;
        }

        public IPropertyBuilder<TEntity> AsStatic()
        {
            builder.staticColumns.Add(property);
            return this;
        }

        public IPropertyBuilder<TEntity> AsCounter()
        {
            builder.counterColumns.Add(property);
            return this;
        }

        public IPropertyBuilder<TEntity> AsIndexed()
        {
            builder.indexedProperties.Add(property);
            return this;
        }

        public IPropertyBuilder<TEntity> WithConverter<T>() where T : ICassandraConverter
        {
            builder.converters.Add(property, Activator.CreateInstance<T>());
            return this;
        }

        public IPropertyBuilder<TEntity> WithProperty<TProp>(Expression<Func<TEntity, TProp>> selector)
            => builder.WithProperty(selector);
    }

    private static PropertyInfo GetPropertyInfo<TProperty>(Expression<Func<TEntity, TProperty>> expr)
    {
        if (expr.Body is MemberExpression member)
            return (PropertyInfo)member.Member;
        if (expr.Body is UnaryExpression { Operand: MemberExpression inner })
            return (PropertyInfo)inner.Member;
        throw new ArgumentException("Expression must be a property access", nameof(expr));
    }

    public EntityMetadata Build()
    {
        if (string.IsNullOrEmpty(tableName))
            throw new InvalidOperationException("Table name is required.");
        if (partitionKeys.Count == 0)
            throw new InvalidOperationException("At least one partition key is required.");

        return new EntityMetadata(
            entityType,
            tableName!,
            keyspace,
            properties,
            partitionKeys.OrderBy(x => x.Key).Select(x => x.Value).ToList(),
            clusteringKeys.OrderBy(x => x.Key).Select(x => x.Value).ToList(),
            columnMappings,
            staticColumns,
            counterColumns,
            indexedProperties,
            converters);
    }
}