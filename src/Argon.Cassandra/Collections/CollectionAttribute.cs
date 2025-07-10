namespace Argon.Cassandra.Collections;


[AttributeUsage(AttributeTargets.Property)]
public class CollectionAttribute(CollectionType collectionType) : Attribute
{
    public CollectionType CollectionType { get; } = collectionType;
    public Type?          KeyType        { get; set; }
    public Type?          ValueType      { get; set; }
}