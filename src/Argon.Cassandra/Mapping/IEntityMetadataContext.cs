namespace Argon.Cassandra.Mapping;

public interface IEntityMetadataContext
{
    IEntityMetadataBuilder<T> ForTable<T>(string? tableName = null, string? keyspace = null) where T : class;
}