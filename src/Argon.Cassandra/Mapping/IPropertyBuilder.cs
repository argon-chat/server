namespace Argon.Cassandra.Mapping;

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